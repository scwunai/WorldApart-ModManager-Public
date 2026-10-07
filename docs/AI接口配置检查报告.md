# WorldApart（不问凡尘）AI 接口配置检查报告

检查时间：2026-10-05
检查对象：`<G>`（WorldApart.exe，Nuverse 出品，Unity IL2CPP）

---

## 一、结论速览

| 项目 | 位置 | 是否可本地编辑 |
|------|------|----------------|
| 玩家自定义 AI 接口配置 | `%USERPROFILE%\AppData\LocalLow\Nuverse\WorldApart\settings.json` 的 `ainpc` 字段组 | 可以（建议改前备份，游戏运行中改会被覆盖） |
| 官方 AI 接口配置 | 不在本地文件中，由游戏服务器下发 | 不可本地编辑 |

---

## 二、玩家自定义接口（游戏内"设置 → AI NPC"界面）

文件：`%USERPROFILE%\AppData\LocalLow\Nuverse\WorldApart\settings.json`

```json
"ainpc": {
  "mode": 0,               // 0 = 官方模式，1 = 自定义接口模式
  "url": "",               // 自定义接口地址（OpenAI 兼容 /chat/completions）
  "apiKey": "",            // 接口密钥
  "model": "",             // 模型名
  "contextSize": 0,        // 上下文长度
  "timeoutSeconds": 0      // 超时时间（秒）
}
```

- 备份副本：`<G>\KIMI\settings_ainpc_备份.json`
- 自定义模式走 OpenAI 兼容协议，程序集中对应 `JNGame.Ainpc.Llm|OpenAiCompatProvider`、`/chat/completions`。
- 对应 UI 资源：`WorldApart_Data\StreamingAssets\yoo\DefaultPackage\43ebec82a22eca18cdc8923bf7ee52f8.bundle`（含 btnAinpcHelp、inputAinpcModel、txtAinpcHint 等界面元素）。

## 三、官方接口配置

官方模式下 AI 请求不走本地配置，而是经游戏官方 API 网关转发：

- 请求路径（取自 `WorldApart_Data\il2cpp_data\Metadata\global-metadata.dat`）：
  - `{apiGateUrl}/AINPC`
  - `/a1/chat/completions`
- 官方模型与转发目标由服务端配置决定，程序中仅有字段名（值为运行时下发，本地无明文）：
  - `OFFICIAL_MODEL`（官方模型）
  - `OFFICIAL_FORWARD_ENDPOINT`（官方转发端点）
  - `OFFICIAL_SUBMIT_MODE`（提交模式）
- 网关地址 `ApiGateUrl` 为运行时从游戏服务器配置获取，本地无任何存放官方接口地址/密钥的配置文件。
- 官方模式的提示文案字段：`SETTINGS_AINPC_HINT_OFFICIAL`（界面内可见官方模式说明与帮助链接 `SETTINGS_AINPC_HELP_URL`）。

## 四、排查过程记录

1. 扫描游戏根目录、`WorldApart_Data\StreamingAssets`、yoo 资源包、`il2cpp_data` 全部文本/二进制。
2. `GameAssembly.dll`（IL2CPP 主程序）中确认 AI 模块程序集 `A1Ainpc.Runtime.dll`（命名空间 `JNGame.Ainpc`）。
3. `global-metadata.dat` 命中 `ainpc`、`openai`、`/v1/chat`、`/chat/completions`、`apiGateUrl/AINPC`、`/a1/chat/completions` 等关键字符串。
4. `%USERPROFILE%\AppData\LocalLow\Nuverse\WorldApart\` 下发现 `settings.json` 含 `ainpc` 配置组、`Mods` 文件夹（当前为空）。
5. `Player.log` 中无 AI 请求记录（当前 mode=0 且可能尚未触发 AI NPC 对话）。

## 五、备注

- 修改 `settings.json` 前请退出游戏或先备份；游戏运行期间保存设置会覆盖该文件。
- 若官方模式不可用，可在游戏内设置中切换为自定义模式，填入自己的 OpenAI 兼容接口（url / apiKey / model）。
