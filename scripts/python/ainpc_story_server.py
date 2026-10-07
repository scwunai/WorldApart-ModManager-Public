# -*- coding: utf-8 -*-
"""WorldApart AI NPC 本地剧情服务器（OpenAI 兼容 /chat/completions）。

用法：
1. 按需编辑 story_config.json（上游模型、提示词改写、台词替换规则）
2. 启动：python ainpc_story_server.py        （默认 127.0.0.1:8399）
3. 游戏 settings.json 的 ainpc 段改为：
     "mode": 1,
     "url": "http://127.0.0.1:8399/v1",
     "apiKey": "story",
     "model": "story",
4. 进游戏与 AI NPC 对话，本服务器负责改写剧情。

支持两种工作方式：
- 上游转发：把请求转给任意 OpenAI 兼容服务（Kimi/DeepSeek/官方代理等），
  在转发前改 system 提示词，在返回后改 NPC 台词/表情/意图。
- 本地兜底：上游不可用时返回固定台词，避免游戏报错。
"""
import json, os, sys, time, urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

BASE = os.path.dirname(os.path.abspath(__file__))
CONF = os.path.join(BASE, "story_config.json")
LOG = os.path.join(BASE, "story_server.log")

EMOTIONS = ["happy", "angry", "doubt", "smile", "think", "sad", "surprise", "afraid", "normal"]


def log(msg):
    line = time.strftime("[%H:%M:%S] ") + msg
    print(line, flush=True)
    with open(LOG, "a", encoding="utf-8") as f:
        f.write(line + "\n")


def load_conf():
    with open(CONF, encoding="utf-8") as f:
        return json.load(f)


def rewrite_messages(messages, conf):
    """在转发前修改 system 提示词（剧情修改的核心入口）。"""
    prefix = conf.get("system_prompt_prefix", "")
    replace_map = conf.get("system_prompt_replace", {})   # 旧文本 -> 新文本
    for m in messages:
        if m.get("role") != "system":
            continue
        c = m.get("content", "")
        for old, new in replace_map.items():
            c = c.replace(old, new)
        if prefix:
            c = prefix + "\n" + c
        m["content"] = c
    return messages


def rewrite_reply(text, conf):
    """返回后改写 NPC 台词。"""
    for old, new in conf.get("reply_replace", {}).items():
        text = text.replace(old, new)
    return text


def parse_npc_json(text):
    """官方返回的是 JSON 字符串；容错解析。"""
    text = text.strip()
    try:
        obj = json.loads(text)
        if isinstance(obj, dict) and "content" in obj:
            return obj
    except Exception:
        pass
    return {"content": text, "emotion": "normal", "control": "none",
            "intent": {"give_gift": False, "gift_item": "none"}}


def sanitize(obj, conf):
    force_emotion = conf.get("force_emotion")
    if force_emotion in EMOTIONS:
        obj["emotion"] = force_emotion
    if obj.get("emotion") not in EMOTIONS:
        obj["emotion"] = "normal"
    obj.setdefault("control", "none")
    obj.setdefault("intent", {"give_gift": False, "gift_item": "none"})
    return obj


def forward_upstream(payload, conf):
    """转发到上游 OpenAI 兼容服务，返回 NPC JSON 对象；失败返回 None。"""
    upstream = conf.get("upstream_url", "").rstrip("/")
    if not upstream:
        return None
    payload = dict(payload)
    payload["messages"] = rewrite_messages(payload.get("messages", []), conf)
    if conf.get("upstream_model"):
        payload["model"] = conf["upstream_model"]
    payload.pop("response_format", None)  # 多数上游不支持 json_schema，剥离
    req = urllib.request.Request(
        upstream + "/chat/completions",
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json",
                 "Authorization": "Bearer " + conf.get("upstream_api_key", "")},
        method="POST")
    try:
        with urllib.request.urlopen(req, timeout=conf.get("upstream_timeout", 60)) as r:
            resp = json.loads(r.read().decode("utf-8"))
        text = resp["choices"][0]["message"]["content"]
        obj = parse_npc_json(text)
        log(f"上游返回: model={resp.get('model')} tokens={resp.get('usage',{}).get('total_tokens')}")
        return obj
    except Exception as e:
        log(f"上游调用失败，改用本地兜底: {e}")
        return None


def local_fallback(payload, conf):
    """不依赖上游时的兜底回复（读取玩家最后一句，做简单应答）。"""
    last_user = ""
    for m in reversed(payload.get("messages", [])):
        if m.get("role") == "user":
            last_user = m.get("content", "")[-200:]
            break
    line = conf.get("fallback_line", "（此人心神一动，似有话要说，却又咽了回去。）")
    log(f"本地兜底回复 | 玩家最后输入: {last_user[:80]!r}")
    return {"content": line, "emotion": "think", "control": "none",
            "intent": {"give_gift": False, "gift_item": "none"}}


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a):  # 静默默认日志
        pass

    def do_POST(self):
        try:
            length = int(self.headers.get("Content-Length", 0))
            payload = json.loads(self.rfile.read(length).decode("utf-8"))
            conf = load_conf()
            log(f"<- {self.command} {self.path} model={payload.get('model')} msgs={len(payload.get('messages',[]))}")

            obj = forward_upstream(payload, conf) or local_fallback(payload, conf)
            obj["content"] = rewrite_reply(obj.get("content", ""), conf)
            obj = sanitize(obj, conf)

            body = {"id": "chatcmpl-story", "object": "chat.completion", "created": int(time.time()),
                    "model": payload.get("model", "story"),
                    "choices": [{"index": 0, "finish_reason": "stop",
                                 "message": {"role": "assistant",
                                             "content": json.dumps(obj, ensure_ascii=False)}}],
                    "usage": {"prompt_tokens": 0, "completion_tokens": 0, "total_tokens": 0}}
            data = json.dumps(body, ensure_ascii=False).encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
            log(f"-> 200 | {obj.get('content','')[:60]!r} emotion={obj.get('emotion')}")
        except Exception as e:
            log(f"错误: {e}")
            self.send_response(500)
            self.end_headers()

    do_GET = do_POST


def main():
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8399
    if not os.path.exists(CONF):
        json.dump({
            "upstream_url": "",
            "upstream_api_key": "",
            "upstream_model": "",
            "upstream_timeout": 60,
            "system_prompt_prefix": "",
            "system_prompt_replace": {"示例旧剧情文本": "示例新剧情文本"},
            "reply_replace": {},
            "force_emotion": "",
            "fallback_line": "（此人心神一动，似有话要说，却又咽了回去。）"
        }, open(CONF, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
        print(f"已生成默认配置 {CONF}，请编辑后重启。")
        return
    srv = ThreadingHTTPServer(("127.0.0.1", port), Handler)
    log(f"剧情服务器启动: http://127.0.0.1:{port} （按 Ctrl+C 停止）")
    srv.serve_forever()


if __name__ == "__main__":
    main()
