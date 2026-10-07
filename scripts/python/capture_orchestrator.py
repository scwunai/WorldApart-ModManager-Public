# -*- coding: utf-8 -*-
"""截获官方 AI 接口的一次完整流程：
1. 启动 mitmdump（127.0.0.1:7890），等待 CA 证书生成
2. 把 mitmproxy CA 装入当前用户受信根证书（不需要管理员）
3. 开启当前用户系统代理（WinINET）
4. 启动游戏；等待 captured_flows.jsonl 出现 AINPC/chat 请求（最多 20 分钟）
5. 抓到后：关代理、停 mitmdump（游戏保持运行，玩家可正常退出）
"""
import json, os, subprocess, sys, time
import winreg

BASE = os.path.dirname(os.path.abspath(__file__))
STATUS = os.path.join(BASE, "capture_status.log")
FLOWS = os.path.join(BASE, "captured_flows.jsonl")
PORT = 7890
GAME = r"<G>\WorldApart.exe"
CA_DIR = os.path.expanduser(r"~\.mitmproxy")
CA_CER = os.path.join(CA_DIR, "mitmproxy-ca-cert.cer")
TIMEOUT = 20 * 60


def log(msg):
    line = time.strftime("[%H:%M:%S] ") + msg
    print(line, flush=True)
    with open(STATUS, "a", encoding="utf-8") as f:
        f.write(line + "\n")


def set_proxy(enable):
    key = r"Software\Microsoft\Windows\CurrentVersion\Internet Settings"
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, key, 0, winreg.KEY_SET_VALUE) as k:
        winreg.SetValueEx(k, "ProxyEnable", 0, winreg.REG_DWORD, 1 if enable else 0)
        if enable:
            winreg.SetValueEx(k, "ProxyServer", 0, winreg.REG_SZ, f"127.0.0.1:{PORT}")
        else:
            try:
                winreg.DeleteValue(k, "ProxyServer")
            except OSError:
                pass
    # 通知系统代理设置已变更
    import ctypes
    INTERNET_OPTION_SETTINGS_CHANGED = 39
    INTERNET_OPTION_REFRESH = 37
    ctypes.windll.wininet.InternetSetOptionW(0, INTERNET_OPTION_SETTINGS_CHANGED, 0, 0)
    ctypes.windll.wininet.InternetSetOptionW(0, INTERNET_OPTION_REFRESH, 0, 0)


def install_ca():
    if not os.path.exists(CA_CER):
        log(f"未找到 CA 证书: {CA_CER}")
        return False
    r = subprocess.run(["certutil", "-user", "-addstore", "Root", CA_CER],
                       capture_output=True, text=True)
    log("certutil: " + (r.stdout.strip() or r.stderr.strip())[:200])
    return r.returncode == 0


def main():
    open(STATUS, "w").close()
    if os.path.exists(FLOWS):
        os.remove(FLOWS)
    log("启动 mitmdump ...")
    mitmdump = os.path.expanduser(
        r"~\AppData\Roaming\kimi-desktop\daimon-share\daimon\runtime\python\.venv\Scripts\mitmdump.exe")
    if not os.path.exists(mitmdump):
        mitmdump = "mitmdump"
    mitm = subprocess.Popen(
        [mitmdump,
         "-s", os.path.join(BASE, "mitm_addon.py"),
         "-p", str(PORT), "--set", "console_eventlog_verbosity=error"],
        stdout=open(os.path.join(BASE, "mitmdump_stdout.log"), "w"),
        stderr=subprocess.STDOUT)
    try:
        for _ in range(60):
            if os.path.exists(CA_CER):
                break
            time.sleep(1)
        if not os.path.exists(CA_CER):
            log("CA 生成超时，退出"); return
        log("CA 就绪，安装到用户根证书区 ...")
        install_ca()
        log("开启系统代理 127.0.0.1:%d ..." % PORT)
        set_proxy(True)
        log("启动游戏 ...")
        subprocess.Popen([GAME], cwd=os.path.dirname(GAME))
        log("等待官方 AI 请求（请与 AI NPC 对话一次），最长 %d 分钟 ..." % (TIMEOUT // 60))
        deadline = time.time() + TIMEOUT
        while time.time() < deadline:
            if os.path.exists(FLOWS) and os.path.getsize(FLOWS) > 0:
                time.sleep(2)
                with open(FLOWS, encoding="utf-8") as f:
                    lines = [l for l in f if l.strip()]
                if lines:
                    try:
                        rec = json.loads(lines[-1])
                        if rec.get("status_code"):
                            log(f"已捕获: {rec['method']} {rec['url']} -> {rec['status_code']}")
                            break
                    except Exception:
                        pass
            time.sleep(3)
        else:
            log("超时，未捕获到 AI 请求")
    finally:
        log("关闭系统代理 ...")
        set_proxy(False)
        log("停止 mitmdump ...")
        mitm.terminate()
        try:
            mitm.wait(timeout=10)
        except Exception:
            mitm.kill()
        log("完成。游戏保持运行，请正常退出游戏。")


if __name__ == "__main__":
    main()
