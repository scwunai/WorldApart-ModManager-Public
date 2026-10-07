# -*- coding: utf-8 -*-
"""mitmproxy 插件：把 AI NPC 相关请求实时写入 JSONL，便于外部轮询。"""
import json, os, time

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "captured_flows.jsonl")
KEYWORDS = ("ainpc", "chat/completions", "a1/chat")


def _redact(v):
    if v is None:
        return None
    v = str(v)
    return v[:8] + "..." if len(v) > 12 else "***"


def _text(content):
    if not content:
        return None
    try:
        return content.decode("utf-8", "replace")
    except Exception:
        return None


class Recorder:
    def response(self, flow):
        url = flow.request.url or ""
        low = url.lower()
        if not any(k in low for k in KEYWORDS):
            return
        req_headers = dict(flow.request.headers)
        if "authorization" in req_headers:
            req_headers["authorization"] = _redact(req_headers["authorization"])
        if "cookie" in req_headers:
            req_headers["cookie"] = _redact(req_headers["cookie"])
        rec = {
            "time": time.strftime("%Y-%m-%d %H:%M:%S"),
            "method": flow.request.method,
            "url": url,
            "request_headers": req_headers,
            "request_body": _text(flow.request.raw_content),
            "status_code": flow.response.status_code if flow.response else None,
            "response_headers": dict(flow.response.headers) if flow.response else None,
            "response_body": _text(flow.response.raw_content) if flow.response else None,
        }
        with open(OUT, "a", encoding="utf-8") as f:
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")


addons = [Recorder()]
