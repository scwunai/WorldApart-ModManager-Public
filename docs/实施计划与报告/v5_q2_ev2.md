# E-V2：RawFilePackage 的 raw 文件加载是否会校验 FileHash / FileCRC？

分析日期：2026-10-06
对象：`WorldApart_Data/StreamingAssets/yoo/RawFilePackage/`（1701 个 `<md5>.mp4` + 二进制清单 `RawFilePackage_1.0.bytes/.json/.hash/.report`）
两条独立证据线：① YooAsset 2.3.9 上游源码；② 本机 `GameAssembly.dll` 反汇编。
两条线结论一致，无冲突。

---

## 1. 结论

**不校验（连长度都不校验）。** 在离线（`OfflinePlayMode`）内置（buildin / StreamingAssets）路径上，游戏加载 raw 文件时对磁盘字节的唯一检查是 `File.Exists(filePath)`（外加一次内存中 `BuildinCatalog` 的字典查表）；清单里的 `FileHash`、`FileCRC`、`FileSize` **在整条 raw 加载路径上一次都没有被读取或比较过**。
因此 **mod 可以直接原地覆盖 `StreamingAssets/yoo/RawFilePackage/<md5>.mp4`**：引擎不会发现、不会拒绝、不会回滚，也不会多读一次文件。需要注意的一点是：**清单本身是被校验的**——`RawFilePackage_1.0.bytes` 会用 `RawFilePackage_1.0.hash` 做一次整包校验，所以"改 mp4"安全，但"改清单（增删条目）而不同时更新 `.hash`"会直接初始化失败。另外若文件被删除，会失败并在日志里打出 `Can not found buildin raw bundle file : <path>`。

这一结论有很强的旁证：全二进制里**唯一**会计算文件内容 CRC 的函数 `FileVerifyHelper.FileVerify` 只有 4 个调用者，全部位于下载缓存文件系统 `DefaultCacheFileSystem`（离线模式下根本不会被实例化）；`FileVerify` 之外的哈希函数——`FileCRC32Safely` / `FileMD5Safely` / `FileSHA1Safely` / 游戏侧 `Game.HashUtility.FileSHA256`——调用者数**均为 0**，即这份游戏二进制里没有任何代码会去哈希一个文件。

---

## 2. 证据线 A：YooAsset 2.3.9 上游源码

Tag `2.3.9` 实测可用（`raw.githubusercontent.com` 直连超时，改用 `https://ghfast.top/https://raw.githubusercontent.com/...` 镜像；树结构由 jsDelivr 的 GitHub 镜像 `https://data.jsdelivr.com/v1/packages/gh/tuyoogame/YooAsset@2.3.9?structure=flat` 取得）。运行时源码位于仓库 `Assets/YooAsset/Runtime/...`。

### 2.1 唯一做决策的地方：`DBFSLoadRawBundleOperation`

`Assets/YooAsset/Runtime/FileSystem/DefaultBuildinFileSystem/Operation/DBFSLoadBundleOperation.cs`
（[源码](https://ghfast.top/https://raw.githubusercontent.com/tuyoogame/YooAsset/2.3.9/Assets/YooAsset/Runtime/FileSystem/DefaultBuildinFileSystem/Operation/DBFSLoadBundleOperation.cs)）

```csharp
internal class DBFSLoadRawBundleOperation : FSLoadBundleOperation
{
    private enum ESteps { None, LoadBuildinRawBundle, Done }
    private readonly DefaultBuildinFileSystem _fileSystem;
    private readonly PackageBundle _bundle;
    private ESteps _steps = ESteps.None;

    internal override void InternalStart()
    {
        DownloadProgress = 1f;
        DownloadedBytes = _bundle.FileSize;      // ← FileSize 仅用于进度显示
        _steps = ESteps.LoadBuildinRawBundle;
    }
    internal override void InternalUpdate()
    {
        if (_steps == ESteps.None || _steps == ESteps.Done)
            return;

        if (_steps == ESteps.LoadBuildinRawBundle)
        {
#if UNITY_ANDROID
            //TODO : 安卓平台内置文件属于APK压缩包内的文件。
            _steps = ESteps.Done;
            Result = new RawBundleResult(_fileSystem, _bundle);
            Status = EOperationStatus.Succeed;
#else
            string filePath = _fileSystem.GetBuildinFileLoadPath(_bundle);
            if (File.Exists(filePath))                       // ← 全部检查就是这一句
            {
                _steps = ESteps.Done;
                Result = new RawBundleResult(_fileSystem, _bundle);
                Status = EOperationStatus.Succeed;
            }
            else
            {
                _steps = ESteps.Done;
                Status = EOperationStatus.Failed;
                Error = $"Can not found buildin raw bundle file : {filePath}";
                YooLogger.Error(Error);
            }
#endif
        }
    }
}
```

无 size 比较、无 CRC、无 Hash。`InternalStart` 里的 `_bundle.FileSize` 只是把进度条直接拉到 100%（`DownloadedBytes`）。

### 2.2 分发点：`DefaultBuildinFileSystem.LoadBundleFile`

`Assets/YooAsset/Runtime/FileSystem/DefaultBuildinFileSystem/DefaultBuildinFileSystem.cs`
（[源码](https://ghfast.top/https://raw.githubusercontent.com/tuyoogame/YooAsset/2.3.9/Assets/YooAsset/Runtime/FileSystem/DefaultBuildinFileSystem/DefaultBuildinFileSystem.cs)）

```csharp
public virtual FSLoadBundleOperation LoadBundleFile(PackageBundle bundle)
{
    if (IsUnpackBundleFile(bundle))
    {
        return _unpackFileSystem.LoadBundleFile(bundle);
    }

    if (bundle.BundleType == (int)EBuildBundleType.AssetBundle)
    {
        var operation = new DBFSLoadAssetBundleOperation(this, bundle);
        return operation;
    }
    else if (bundle.BundleType == (int)EBuildBundleType.RawBundle)
    {
        var operation = new DBFSLoadRawBundleOperation(this, bundle);
        return operation;
    }
    else { /* FSLoadBundleCompleteOperation(error) */ }
}
```

配套的路径解析与整包读取（同文件）：

```csharp
public string GetBuildinFileLoadPath(PackageBundle bundle)
{
    if (_buildinFilePathMapping.TryGetValue(bundle.BundleGUID, out string filePath) == false)
    {
        filePath = PathUtility.Combine(_packageRoot, bundle.FileName);
        _buildinFilePathMapping.Add(bundle.BundleGUID, filePath);
    }
    return filePath;
}

public virtual byte[] ReadBundleFileData(PackageBundle bundle)
{
    if (IsUnpackBundleFile(bundle))
        return _unpackFileSystem.ReadBundleFileData(bundle);

    if (Exists(bundle) == false)          // ← 只是 _wrappers.ContainsKey(bundleGUID)，内存查表
        return null;
    ...
    if (bundle.Encrypted) { /* DecryptionServices.ReadFileData(...) */ }
    else
    {
        string filePath = GetBuildinFileLoadPath(bundle);
        return FileUtility.ReadAllBytes(filePath);   // ← 原样返回磁盘字节
    }
}
```

`IsUnpackBundleFile` 在非 Android 下恒为 `false`：

```csharp
protected bool IsUnpackBundleFile(PackageBundle bundle)
{
    if (Belong(bundle) == false) return false;
#if UNITY_ANDROID
    if (bundle.Encrypted) return true;
    if (bundle.BundleType == (int)EBuildBundleType.RawBundle) return true;
    return false;
#else
    return false;                 // ← 本机是 Windows 独立版，走这条
#endif
}
```

即 Windows 上既不走解压文件系统，也不会去 `DefaultCacheFileSystem` 的校验机制。

### 2.3 句柄/Provider 层：没有任何校验

- `RawFileHandle.GetRawFilePath()` → `Provider.BundleResultObject.GetBundleFilePath()`；
  `RawFileHandle.GetRawFileData()` → `Provider.BundleResultObject.ReadBundleFileData()`。
  两个都只是转发到 `RawBundleResult`，而 `RawBundleResult` 又原样转发给文件系统：

```csharp
internal class RawBundleResult : BundleResult
{
    public override string GetBundleFilePath()  { return _fileSystem.GetBundleFilePath(_packageBundle); }
    public override byte[] ReadBundleFileData() { return _fileSystem.ReadBundleFileData(_packageBundle); }
    public override string ReadBundleFileText() { return _fileSystem.ReadBundleFileText(_packageBundle); }
}
```

- `RawFileProvider` 干脆什么都不做：

```csharp
protected override void ProcessBundleResult()
{
    InvokeCompletion(string.Empty, EOperationStatus.Succeed);
}
```

`ProviderOperation` 中唯一用到 `FileSize` 的地方是下载进度累加（`status.TotalBytes += bundleLoader.LoadBundleInfo.Bundle.FileSize;`），是上报不是比较。

### 2.4 `EFileVerifyLevel` 确实存在——但 buildin raw 路径从不咨询它

`Assets/YooAsset/Runtime/FileSystem/EFileVerifyLevel.cs`：

```csharp
public enum EFileVerifyLevel
{
    Low = 1,     // 验证文件存在
    Middle = 2,  // 验证文件大小
    High = 3,    // 验证文件大小和CRC
}
```

`Assets/YooAsset/Runtime/FileSystem/FileVerifyHelper.cs`（唯一会碰文件内容的函数，全文）：

```csharp
public static EFileVerifyResult FileVerify(string filePath, long fileSize, string fileCRC, EFileVerifyLevel verifyLevel)
{
    try
    {
        if (File.Exists(filePath) == false)
            return EFileVerifyResult.DataFileNotExisted;
        long size = FileUtility.GetFileSize(filePath);
        if (size < fileSize)       return EFileVerifyResult.FileNotComplete;
        else if (size > fileSize)  return EFileVerifyResult.FileOverflow;
        if (verifyLevel == EFileVerifyLevel.High)         // ← 只有 High 才真算 CRC32
        {
            string crc = HashUtility.FileCRC32(filePath);
            if (crc == fileCRC) return EFileVerifyResult.Succeed;
            else                return EFileVerifyResult.FileCrcError;
        }
        else return EFileVerifyResult.Succeed;            // ← Low / Middle 只比大小
    }
    catch (Exception) { return EFileVerifyResult.Exception; }
}
```

`DefaultBuildinFileSystem` 里虽然声明了 `public EFileVerifyLevel FileVerifyLevel { private set; get; } = EFileVerifyLevel.Middle;`，但**只在 `OnCreate` 里把它转发给解压文件系统**，自己从不消费：

```csharp
// DefaultBuildinFileSystem.OnCreate
_unpackFileSystem.SetParameter(FileSystemParametersDefine.FILE_VERIFY_LEVEL, FileVerifyLevel);
```

`FileVerifyHelper.FileVerify` 在 2.3.9 全部调用点（源码层面 3 处，见下）都落在缓存系统：

| 文件 | 方法 | 级别 | 时机 |
|---|---|---|---|
| `DefaultCacheFileSystem.cs` | `VerifyCacheFile` | 硬编码 `High` | cache 读回退（仅 AssetBundle 加载失败后） |
| `VerifyTempFileOperation.cs` | `VerifyInThread` | 硬编码 `High` | 下载完成、写入 cache 之前的临时文件 |
| `VerifyCacheFilesOperation.cs` | `VerifyingCacheFile` | `_fileSystem.FileVerifyLevel`（默认 `Middle`） | cache FS 初始化扫描已存在文件 |

`DefaultCacheFileSystem` 的 **raw** 分支 `DCFSLoadRawBundleOperation` 同样只做 `File.Exists(filePath)`，不校验（这也意味着即使在 Android 上被迫走解压/缓存路径，raw 文件依然不会被 CRC 校验）。

### 2.5 离线模式的组成：只有一个 `DefaultBuildinFileSystem`

`Assets/YooAsset/Runtime/InitializeParameters.cs`：

```csharp
public class OfflinePlayModeParameters : InitializeParameters
{
    public FileSystemParameters BuildinFileSystemParameters;   // ← 只有一个字段
}
```

`DefaultCacheFileSystem`（以及上表所有 CRC 机制）在 `OfflinePlayMode` 下**不会被创建**。

### 2.6 `Encrypted` 的处理

`bundle.Encrypted` 只用于在 `ReadBundleFileData/ReadBundleFileText` 里选择走 `DecryptionServices` 还是 `FileUtility.ReadAllBytes`。本包 `.report` 明确 `EncryptionClassName: "null"`、`EncryptedBundleTotalCount: 0`，且清单里每个 bundle 的 `"Encrypted": false`，所以走的是朴素的 `File.ReadAllBytes`。即便走解密分支，`FileLoadCRC = bundle.UnityCRC`（Unity AssetBundle 的 CRC，RawFileBuildPipeline 恒为 0）也只是作为参数交给用户的解密实现，YooAsset 自己不比较。

---

## 3. 证据线 B：本机 `GameAssembly.dll` 反汇编

反汇编工具：项目 `tools/venv` 下的 capstone 脚本 + 符号从 `il2cpp_out/dump.cs` 解析、字符串从 `il2cpp_out/stringliteral.json` 解析。所有 VA = RVA + `0x180000000`。
（下文"调用者扫描"是自己写全 `.text` 段扫 `E8 rel32` 得到的；仓库自带的 `--callers` 默认只扫 RVA `0x900000..0xE80000`，而 YooAsset 代码在 `0x6600000` 附近，所以该子命令对 YooAsset 恒返回"无调用者"，不要用它下结论。）

### 3.1 游戏确实以 OfflinePlayMode + 仅 buildin FS 初始化 RawFilePackage

`FsmInitializePackage.<InitRawFilePackage>d__5.MoveNext` @ RVA `0x9DC810`，寄存器 `ebx` = playMode：

```
0x1809dca14  cmp ebx, 1                      ; 1 == OfflinePlayMode
0x1809dca17  jne 0x1809dcaa1
...
0x1809dca3d  call 0x186617b20                ; FileSystemParameters.CreateDefaultBuildinFileSystemParameters(null, null)
0x1809dca4f  mov qword ptr [rbx + 0x18], rax ; OfflinePlayModeParameters.BuildinFileSystemParameters (+0x18)
0x1809dca64  mov rcx, qword ptr [rip + ...]  ; Il2CppClass* for System.Boolean (装箱)
0x1809dca6b  mov byte ptr [rsp + 0x40], 1    ; value = true
0x1809dca70  call 0x180572980                ; box
0x1809dca7e  mov rdx, qword ptr [rip + ...]  ; rip->RVA 0x8222290  STR='APPEND_FILE_EXTENSION'
0x1809dca8e  call 0x186617ab0                ; FileSystemParameters.AddParameter
0x1809dcbef  call 0x186632bb0                ; ResourcePackage.InitializeAsync
```

要点：OfflinePlayMode 分支**只写 `+0x18`**（对比 `cmp ebx,2`/HostPlayMode 分支会同时写 `+0x18` 与 `+0x20` 两个 FS 参数），并且**只加了 `APPEND_FILE_EXTENSION=true` 这一个自定义参数**。
全 `.text` 扫 `FileSystemParameters.AddParameter` 只有 3 个调用点：`0x9DCA8E`、`0x9DCB6E`（都在上面这个协程里，key 都是 `APPEND_FILE_EXTENSION`）和 `0x161101E`（`DlcPackageMounter.<InitializePackageAsync>d__25`，DLC 通道）。
另外 `FILE_VERIFY_LEVEL` 这个字符串字面量（RVA `0x81FC2D0`）在代码里只被 `DefaultBuildinFileSystem.OnCreate/SetParameter` 与 `DefaultCacheFileSystem.SetParameter` 引用（就是 `SetParameter` 的 key 比较），**没有任何地方把它传给 buildin FS**。所以 buildin FS 的 `FileVerifyLevel` 保持默认值。

### 3.2 默认 `FileVerifyLevel` 与 `InstallClearMode`

`DefaultBuildinFileSystem..ctor` @ RVA `0x6604920`：

```
0x1866049d5  mov dword ptr [rdi + 0x38], 2   ; FileVerifyLevel = EFileVerifyLevel.Middle (2)
0x1866049df  mov dword ptr [rdi + 0x3c], 3   ; InstallClearMode  = ClearAllManifestFiles (3)
```

与源码 `= EFileVerifyLevel.Middle` 一致。（再次强调：buildin FS 自己不用这个值，只在 `OnCreate` 里转发给解压 FS。）

### 3.3 分发：bundle 类型 2/3

`EBuildBundleType`（dump.cs，TypeDefIndex 25605）：

```
Unknown = 0;  VirtualBundle = 1;  AssetBundle = 2;  RawBundle = 3;
```

而 `PackageBundle.InitBundle` @ RVA `0x662B860` 把所有 bundle 的 `_bundleType` 统一取自**包级**的 `BuildBundleType`：

```
0x18662b885  mov eax, dword ptr [rdi + 0x20]   ; manifest.BuildBundleType
0x18662b88e  mov dword ptr [rbx + 0x50], eax   ; PackageBundle._bundleType
```

实测：`RawFilePackage_1.0.json` → `BuildBundleType: 3`；`DefaultPackage_1.0.json` → `BuildBundleType: 2`。两者与上面的枚举完全对应。

`DefaultBuildinFileSystem.LoadBundleFile` @ RVA `0x6603960`（函数体从 `0x1866039D4` 开始）：

```
0x1866039d4  mov rax, qword ptr [rsi]        ; 取 this 的 Il2CppClass*
0x1866039e4  call qword ptr [rax + 0x348]    ; 虚调用 Belong(bundle)
                                             ;   —— 这就是被内联的 IsUnpackBundleFile() 的第一句；
                                             ;   非 Android 分支紧接着 return false，所以返回值未被使用，
                                             ;   解压 FS 的委派分支在本平台构建里被整段消除
0x186603ea  test rdi, rdi                    ; 空 bundle 检查
0x186603f3  cmp dword ptr [rdi + 0x50], 2    ; BundleType == AssetBundle(2)?
0x186603fc  je 0x186603a71                   ;   → 走 AssetBundle 分支
0x186603fe  cmp dword ptr [rdi + 0x50], 3    ; BundleType == RawBundle(3)?
0x18660402  je 0x186603a68                   ;   → 走 Raw 分支
0x18660404  ...                              ; 其他类型：String.Format 错误信息 + FSLoadBundleCompleteOperation
0x186603a7d  xor edx, edx                    ; 两个分支在此汇合：object_new
0x186603a85  call 0x186617920                ; FSLoadBundleOperation 基类构造
0x186603a91  mov qword ptr [rbx + 0x88], rsi ; _fileSystem = this   (0x88)
0x186603aa7  mov qword ptr [rbx + 0x90], rdi ; _bundle     = bundle (0x90)
```

`0x88/0x90` 恰好是 `DBFSLoadRawBundleOperation` 的 `_fileSystem`/`_bundle` 偏移（`_steps` 在 `0x98`）。而 `cmp ...,2` 分支指向的操作体 `DBFSLoadAssetBundleOperation.InternalUpdate` @ `0x65FFCF0` 里调用的是 `GetBuildinFileLoadPath` + `AssetBundle.LoadFromFileAsync` / `AssetBundle.LoadFromFile`（+ `AsyncOperation.get_isDone` / `AssetBundleCreateRequest.get_assetBundle`），确认 2 = AssetBundle、3 = RawBundle。

### 3.4 raw 分支的决策指令（关键）

`DBFSLoadRawBundleOperation.InternalStart` @ RVA `0x6600720`：

```
0x186600724  mov rax, qword ptr [rcx + 0x90]   ; _bundle
0x18660072b  mov dword ptr [rcx + 0x78], 0x3f800000  ; DownloadProgress = 1.0f
0x186600737  mov rax, qword ptr [rax + 0x30]   ; _bundle.FileSize   (PackageBundle+0x30)
0x18660073b  mov qword ptr [rcx + 0x80], rax   ; DownloadedBytes = FileSize  ← 仅进度
0x186600742  mov dword ptr [rcx + 0x98], 1     ; _steps = LoadBuildinRawBundle
```

`DBFSLoadRawBundleOperation.InternalUpdate` @ RVA `0x6600760`：

```
0x186600791  cmp dword ptr [rbp + 0x98], 0     ; _steps == None?
0x186600798  je  0x18660082c                    ;   → return
0x18660079e  cmp dword ptr [rbp + 0x98], 2     ; _steps == Done?
0x1866007a5  je  0x18660082c                    ;   → return
0x1866007ab  cmp dword ptr [rbp + 0x98], 1     ; _steps == LoadBuildinRawBundle?
0x1866007b2  jne 0x18660082c
0x1866007b4  mov rcx, qword ptr [rbp + 0x88]   ; _fileSystem
0x1866007c4  mov rdx, qword ptr [rbp + 0x90]   ; _bundle
0x1866007d3  call 0x1866035f0                  ; DefaultBuildinFileSystem.GetBuildinFileLoadPath(bundle)
0x1866007d8  xor edx, edx
0x1866007da  mov rcx, rax
0x1866007dd  mov rbx, rax
0x1866007e0  call 0x18544ee90                  ; System.IO.File.Exists(filePath)      <<< 唯一的检查
0x1866007e5  mov dword ptr [rbp + 0x98], 2     ; _steps = Done
0x1866007ef  test al, al
0x1866007f1  jne 0x186600832                   ; 存在 → 成功分支
0x1866007f3  mov dword ptr [rbp + 0x38], 3     ; Status = Failed(3)
0x1866007fd  mov rcx, qword ptr [rip + ...]    ; rip->RVA 0x818B9C8
                                               ;   STR = 'Can not found buildin raw bundle file : '
0x186600807  call 0x1852cb3e0                  ; String.Concat(path)
0x186600822  call 0x18663a790                  ; YooLogger.Error
...
0x186600832  mov rcx, qword ptr [rip + ...]    ; object_new
0x186600865  call 0x18065fd90                  ; = RawBundleResult(IFileSystem, PackageBundle) 的二参构造
0x186600889  mov dword ptr [rbp + 0x38], 2     ; Status = Succeed(2)
```

**判据落点非常明确：**
- `call 0x18544ee90`（`System.IO.File.Exists`）之后 `test al, al` / `jne` —— **存在即通过**。
- 唯一的分支条件是"文件是否存在"：**存在 → `Status = Succeed`，把 `RawBundleResult(fs, bundle)` 放进 `Result`；不存在 → `Status = Failed` 并打日志**。
- 这条路径上没有任何 `FileUtility.GetFileSize` / `HashUtility.FileCRC32` / 字符串比较的调用。
- 失败分支里的字符串 `Can not found buildin raw bundle file : `（字面量 RVA `0x818B9C8`）与源码 2.1 节 `DBFSLoadRawBundleOperation` 的 `Error` 文案逐字一致，**这条反汇编就是在证明"这个函数就是 DBFSLoadRawBundleOperation"**。

### 3.5 句柄 → 结果对象 → 文件系统（确认 mp4 是"按路径"交给播放器）

```
RawFileHandle.GetRawFilePath  @0x66200C0
  → Provider.BundleResultObject ([provider+0xA0]) 虚调用 槽位 [vtable+0x188]
RawBundleResult.GetBundleFilePath @0x660A660
  → IFileSystem.GetBundleFilePath 虚调用（记录常量 ecx = 0x11 = IFileSystem 槽 17）
DefaultBuildinFileSystem.GetBundleFilePath @0x6603750
  → jmp DefaultBuildinFileSystem.GetBuildinFileLoadPath @0x66035F0
```

`GetBuildinFileLoadPath` @ `0x66035F0`：

```
0x18660363f  mov r9, ...
0x186603646  lea r8, [rsp + 0x30]
0x18660364b  mov rdx, qword ptr [rdi + 0x20]   ; bundle.BundleGUID  (== PackageBundle.FileHash)
0x18660364f  call 0x1822d2e00                  ; Dictionary<string,string>.TryGetValue(_buildinFilePathMapping)
0x186603654  test al, al
0x186603656  jne 0x18660369f                   ; 命中 → 直接返回（无任何落盘检查）
0x186603666  call 0x18662ba70                  ; PackageBundle.get_FileName
0x186603674  call 0x18662d3e0                  ; PathUtility.Combine(_packageRoot /*[rsi+0x28]*/, FileName)
0x18660369a  call 0x1822c4960                  ; 写入 _buildinFilePathMapping 缓存
```

注意 `PackageBundle.BundleGUID => FileHash`（`get_BundleGUID` @ `0x6581C0` 就是 `mov rax,[rcx+0x20]; ret`，而 `FileHash` 字段在 `0x20`）。所以 `FileHash` 在这里的用途是**身份/命名**（BuildinCatalog 的 key、字典缓存 key），**不是内容比较**。真实文件名来自 `BuildinCatalog.json/.bytes`（`BundleGUID → FileName`，例如 `0005ad2aea4ac6d67f660e87e451d90d → "0005ad....mp4"`）。

`RawFileHandle.GetRawFileData` @ `0x6620070` 走 `RawBundleResult.ReadBundleFileData` @ `0x660A7F0` → `DefaultBuildinFileSystem.ReadBundleFileData` @ `0x66041B0`，后者在 `0x186604259` 调用 `FileUtility.ReadAllBytes`（`0x662A0F0`），同样是原样读字节。

### 3.6 全二进制级的"没有校验代码"证明

这是最强的静态证据。用全 `.text` 段 `E8 rel32` 扫描得到：

| 函数 | RVA | 直接调用者数 | 调用者 |
|---|---|---|---|
| `FileVerifyHelper.FileVerify` | `0x6618650` | **4** | `DCFSLoadAssetBundleOperation.InternalUpdate.+0x786`、`DefaultCacheFileSystem.VerifyCacheFile`、`VerifyCacheFilesOperation.VerifyingCacheFile`、`VerifyTempFileOperation.VerifyInThread` |
| `FileUtility.GetFileSize` | `0x662A070` | **1** | `FileVerifyHelper.FileVerify.+0x26` |
| `HashUtility.FileCRC32` | `0x662A440` | **2** | `FileVerifyHelper.FileVerify.+0x43`、`HashUtility.FileCRC32Safely.+0x6` |
| `HashUtility.FileCRC32Safely` | `0x662A400` | **0** | — |
| `HashUtility.FileMD5` | `0x662A5E0` | **1** | 只有自己的 `FileMD5Safely.+0x6` |
| `HashUtility.FileMD5Safely` | `0x662A5A0` | **0** | — |
| `HashUtility.FileSHA1Safely` | `0x662A740` | **0** | — |
| `Game.HashUtility.FileSHA256` | `0x181AA95A0` | **0** | — |
| `Game.HashUtility.BytesSHA256` | `0x181AA9470` | **0** | — |

四个 `FileVerify` 调用者**全部在缓存文件系统**，`DefaultBuildinFileSystem` / `DBFS*` 一个都没有。而 `BytesMD5`/`BytesCRC32`（对内存 buffer，不是文件）各有 4 个调用者，全部是清单校验：`LoadEditorPackageManifestOperation`、`LoadWebRemotePackageManifestOperation`、`LoadWebServerPackageManifestOperation`、`ManifestTools.VerifyManifestData`。

顺带把 `FileVerifyHelper.FileVerify` 本体也反汇编出来，确认枚举语义与源码 2.4 一致：

```
== rva 0x6618650  FileVerifyHelper.FileVerify(filePath, fileSize, fileCRC, verifyLevel)
0x186618668  call 0x18544ee90            ; File.Exists
0x18661866f  je   0x1866186c6            ;   → eax = 0xfffffffc (-4 DataFileNotExisted)
0x186618676  call 0x18662a070            ; FileUtility.GetFileSize
0x18661867b  cmp  rax, rdi
0x18661867e  jl   0x1866186bf            ;   → -3 FileNotComplete（比清单小）
0x186618680  jg   0x1866186b8            ;   → -2 FileOverflow （比清单大）
0x186618682  cmp  esi, 3                 ; verifyLevel == High(3) ?
0x186618685  je   0x18661868e
0x186618687  mov  eax, 1                 ;   Low/Middle：直接 Succeed（只比过大小）
0x186618693  call 0x18662a440            ; HashUtility.FileCRC32(filePath)
0x1866186a1  call 0x1852cdb20            ; String.op_Equality(crc, fileCRC)
0x1866186a8  jne  0x1866186aa
0x1866186aa  mov  eax, 0xffffffff        ;   -1 FileCrcError
0x1866186b1  mov  eax, 1                 ;    1 Succeed
```

即：**Low 和 Middle 都只比大小，只有 High 才算 CRC32**。而这条路径（唯一会内容校验的路径）与 buildin raw 加载毫无交集。

### 3.7 谁会调用 raw 句柄（确认"交给播放器的是路径"）

| 函数 | 直接调用者数 | 调用者 |
|---|---|---|
| `RawFileHandle.GetRawFilePath` (`0x66200C0`) | **5** | `CorePassiveEquipPanel.<>c__DisplayClass74_0.<PlaySkillCG>b__0`、`SupportSkillEquipPanel.<>c__DisplayClass77_0.<PlaySkillCG>b__0`、`GameMediaPathResolver.<>c__DisplayClass0_0.<ResolveAsync>b__0`、`VideoAnimation.<>c__DisplayClass86_0.<LoadRawFileSourceAsync>b__0`、`UPFPlayerAttrPortraitVideo.<>c__DisplayClass46_0.<ResolvePathAsync>b__0` |
| `RawFileHandle.GetRawFileData` (`0x6620070`) | **0** | — |
| `RawFileHandle.GetRawFileText` (`0x662140`) | **0** | — |
| `ResourcePackage.LoadRawFileAsync` (`0x1866338B0`) | **1** | `GameResourceManager.LoadRawFileForVideoPlayer.+0x2E5` |
| `GameResourceManager.LoadRawFileForVideoPlayer` (`0x79E240`) | **5** | `CorePassiveEquipPanel.PlaySkillCG`、`SupportSkillEquipPanel.PlaySkillCG`、`GameMediaPathResolver.<ResolveAsync>d__0.MoveNext`、`VideoAnimation.LoadRawFileSourceAsync`、`UPFPlayerAttrPortraitVideo.<ResolvePathAsync>d__46.MoveNext` |

完整调用链（与题面 anchor 完全吻合）：

```
Game 侧 5 个消费方（VideoAnimation / GameMediaPathResolver / UPFPlayerAttrPortraitVideo / 两个 PlaySkillCG）
  → GameResourceManager.LoadRawFileForVideoPlayer(location, cb, priority)          @0x79E240
      0x18079e50a  call YooAssets.GetPackage
      0x18079e525  call ResourcePackage.LoadRawFileAsync(location, 0)              @0x1866338B0
      0x18079e56a  call RawFileHandle.add_Completed                              @0x186620190
      （失败分支：0x18079e3fd Debug.LogWarning，字符串 'Failed to load raw file ! ' @0x8217558）
      回调 GameResourceManager.<>c__DisplayClass18_0.<LoadRawFileForVideoPlayer>b__0  @0x79EB20
  → ResourceManager.LoadRawFileAsync → RawFileProvider（ProcessBundleResult 直接 Succeed）
  → DefaultBuildinFileSystem.LoadBundleFile → DBFSLoadRawBundleOperation（只有 File.Exists）
  ← 回调里取 RawFileHandle.GetRawFilePath() → 磁盘绝对路径 → 交给 AVPro MediaPlayer
```

`GetRawFileData`/`GetRawFileText` 调用者为 0，说明游戏**不会**把 mp4 读进内存再喂给播放器——它是把磁盘路径交给 AVPro（`AVProVideoTimelineController` 持有 `MediaPlayer`，见 dump.cs）。这进一步排除了"读字节时被校验"的可能。

### 3.8 清单侧：确实有校验（与 mp4 无关，但影响 mod 的可行边界）

`ManifestTools.VerifyManifestData` @ RVA `0x661D780`（内部用 `HashUtility.BytesMD5` / `BytesCRC32`）的直接调用者只有 2 个：

- `LoadBuildinPackageManifestOperation.InternalUpdate` @ `0x6609B60` + `0x1C4`
- `LoadCachePackageManifestOperation.InternalUpdate` @ `0x660A1A0` + `0x117`

并且 `LoadBuildinPackageManifestOperation.ESteps` = `{ None, RequestFileData, VerifyFileData, LoadManifest, Done }`（dump.cs TypeDefIndex 25428），`LoadBuildinPackageManifestOperation..ctor(fileSystem, packageVersion, packageHash)` 会接收 `_packageHash`（即 `RawFilePackage_1.0.hash` 的内容 `4f5c20a7`）。

**所以：清单二进制被校验，清单里列出的各个 mp4 不被校验。** 这条在两条证据线上都成立（源码 2.7 节 / 二进制 3.8 节），无冲突。

---

## 4. 实证旁证：清单里的 FileCRC / FileSize 是"真值"

对 `RawFilePackage_1.0.json` 的 `BundleList` 全量核对（1701 条）：

- `FileSize`：1701/1701 与磁盘文件大小**完全相等**。
- `FileCRC`：1701/1701 等于磁盘文件 CRC32 的**字节序反转**十六进制表示（例如清单 `d9ba8c35` ↔ `zlib.crc32(file) = 0x358cbad9`，`7fe9e371` ↔ `0x71e3e97f`；1701 条全部满足 `bytes.fromhex(actual)[::-1].hex() == manifest_crc`），`crc_equal` 为 0。

意义：清单里的校验值确实是内容真值（YooAsset 构建期由 `TaskUpdateBundleInfo_RFBP.GetBundleFileCRC()` 用 `HashUtility.FileCRC32` 算出，序列化时按字节倒序），**所以它"本来能够"发现内容篡改——只是运行时从来没有去比**。这也排除了"因为 CRC 记错了所以比不出来"这种解释。

---

## 5. 两条证据线的对照

| 断言 | 源码 | 二进制 | 一致？ |
|---|---|---|---|
| buildin raw 加载只做 `File.Exists` | `DBFSLoadRawBundleOperation.InternalUpdate` | `0x1866007E0 call File.Exists` + `test al,al` + Succeed/Failed | ✅ |
| 失败文案 | `Can not found buildin raw bundle file : {filePath}` | 字面量 `0x818B9C8` 内容逐字相同 | ✅ |
| `FileSize` 只用于进度 | `DownloadedBytes = _bundle.FileSize` | `[bundle+0x30] → [op+0x80]`，无比较 | ✅ |
| `FileVerifyLevel` 默认 Middle、不参与 buildin | ctor `= Middle`；仅转发给 unpack FS | ctor `[rdi+0x38]=2`；`FILE_VERIFY_LEVEL` 无 AddParameter 调用点 | ✅ |
| 离线模式只有 buildin FS | `OfflinePlayModeParameters` 仅 1 个字段 | `cmp ebx,1` 分支只写 `+0x18` | ✅ |
| RawBundle = 3 / AssetBundle = 2 | `EBuildBundleType` | `cmp [bundle+0x50],2/3` + manifest `BuildBundleType` 2/3 | ✅ |
| 清单被校验 | `ManifestTools.VerifyManifestData` | 仅 2 个调用者（buildin/cache 清单加载） | ✅ |
| 校验函数只在 cache 路径 | 3 处调用点全在 cache | `FileVerify` 4 个调用者全在 cache | ✅ |

**没有任何冲突项。** 唯一一处我一开始以为存在分歧的地方是"buildin 清单是否校验"：子代理引用的 `LoadBuildinPackageManifestOperation.cs` 中 `ESteps.VerifyFileData` 分支一度被我误判为属于 `DBFSLoadPackageManifestOperation`（后者只有 `RequestBuildinPackageHash/LoadBuildinPackageManifest/Done` 三个步骤）。复核 dump.cs 后确认 `LoadBuildinPackageManifestOperation.ESteps` 确实含 `VerifyFileData = 2`，二进制里 `ManifestTools.VerifyManifestData` 的调用者也确实包含它——**是看错了一行，不是真分歧**。

---

## 6. 对 mod 的直接影响

1. **可以原地覆盖**。`WorldApart_Data/StreamingAssets/yoo/RawFilePackage/<md5>.mp4` 直接用同名字节替换即可，无需注入、无需改清单、无需改 `.hash`。`GetRawFilePath()` 返回的就是这个路径，AVPro 拿到的就是新文件。
2. **不需要保持长度**。举最坏情况的反证：即便真有校验，默认档位也只会比大小；但这套逻辑跟 buildin raw 毫无交集，所以长短随意。
3. **唯一会被发现的破坏方式是"删文件/改名"**：会走到 `Status = Failed` + `Error = "Can not found buildin raw bundle file : <path>"`，游戏侧再 `Debug.LogWarning("Failed to load raw file ! ")` 并回退到 `ResolveExternalRawFilePackage` 的 DLC 通道。
4. **不要顺手改清单**：`RawFilePackage_1.0.bytes` 会被 `RawFilePackage_1.0.hash` 校验。若要新增映射（例如把某个空转的地址指向新 mp4），必须同时重算 `.hash`（32 位=MD5、8 位=CRC32，两条路径见 `ManifestTools.VerifyManifestData`）。
5. `<md5>.mp4` 里的 `md5` **不是**内容的 MD5，而是构建期 `FileHash`（`HashUtility.FileMD5` 于构建时算出）。运行时它只当 BundleGUID/字典 key 用，所以文件名与内容不匹配也无人过问——但重新构建时不要指望游戏会自动发现。
6. `BuildinCatalog.json` 与 `BuildinCatalog.bytes` 只存 `BundleGUID → FileName`，`DisableCatalogFile=false` 且游戏没设 `DISABLE_CATALOG_FILE`，所以**新增 mp4 文件不会自动被识别**；要新增必须改清单（并重算 `.hash`）或走既有的地址映射。

---

## 7. 无法确定的项（Explicit "could not determine"）

1. **没有动态验证。** 本次严格遵守"不碰游戏进程"，全部结论来自静态反汇编 + 源码 + 磁盘文件实证。因此"运行时确实按这条路径走"是由静态调用链（`LoadRawFileForVideoPlayer` 未被裁剪、5 个消费方都真实存在、字符串字面量都在）推出的，而不是观测到的。
2. **未逐条反汇编 5 个游戏侧消费方**（`VideoAnimation` / `GameMediaPathResolver` / `UPFPlayerAttrPortraitVideo` / 两个 `PlaySkillCG`），只确认它们都取 `GetRawFilePath()`。不过这一层不可能自行哈希 mp4：全二进制里 `Game.HashUtility.FileSHA256`/`BytesSHA256` 调用者为 0，`FileUtility.ReadAllBytes`(YooAsset) 的 6 个调用者里与 raw 相关的只有 buildin/cache 的 `ReadBundleFileData`。**残留风险不为零但极低。**
3. **`AppendFileExtension=true` 对最终路径的精确影响未逐字节验证。** `get_FileName` @ `0x662BA70` 走 `ManifestTools.GetRemoteBundleFileName(...)`，其中 `_fileExtension` 由 `ManifestTools.GetRemoteBundleFileExtension(bundle.BundleName)` 决定。本包的 `BuildinCatalog` 已经给出 `BundleGUID → "<hash>.mp4"` 的显式映射并在 `GetBuildinFileLoadPath` 里优先命中，所以字典路径覆盖了 `PathUtility.Combine(_packageRoot, bundle.FileName)` 这条 fallback；**若某条目的 BundleGUID 不在 Catalog 里，fallback 生成的文件名我没有实测**。
4. **`DefaultUnpackFileSystem` 仍然会被创建并初始化。** 源码里 `DefaultBuildinFileSystem.OnCreate` 无条件 `new DefaultUnpackFileSystem()` 并 `OnCreate(packageName, null)`（沙盒根 `WorldApart_Data/yoo/RawFilePackage/`），其初始化会跑 `VerifyCacheFilesOperation`（`FileVerifyLevel=Middle`，只比大小）。目前该目录下只有它自己的 `UnpackManifestFiles/ApplicationFootPrint.bytes`，而 StreamingAssets 里的 mp4 不在它的作用域内（`IsUnpackBundleFile` 在 Windows 恒 `false`，不会导入）。**我没有动态确认它初始化时到底扫了哪些文件**；结论不受影响（即使扫到 raw，也走不到 `DBSF*` 的加载路径，且只有大小校验）。
5. **`HashUtility` 系列的字符串/字节重载**（`StringMD5`/`StreamMD5`/`StreamCRC32`/`StringCRC32`）调用者均为 0，所以已排除；但我没有穷举 IL2CPP 里可能存在的**间接调用**（例如通过委托/反射调用 `FileSHA256`）。`--callers` 只能发现直接 `E8 rel32` 调用；对于 0 调用者的函数，若存在间接调用会漏掉。考虑到这些函数本身是 `private`/`static` 且 dump 里没有对应的委托签名，我认为可能性可忽略。
6. **`RawFilePackage_1.0.bytes` 与 `.json` 是否内容等价**（前者是运行时真正加载的二进制清单）没有逐条比对。`.report` 与 `.json` 的 `AssetFileTotalCount: 1701` / `AllBundleTotalCount: 1701` 与磁盘 1701 个 mp4、`BundleList` 1701 条一致，且 `DependBundleIDs` 全空，我据此认为二者一致；若 mod 要改清单，务必直接操作 `.bytes`。

---

## 8. 复现命令（不含任何写盘副作用）

```
# 清单结构 / 每个 bundle 的 FileHash-FileCRC-FileSize
python -c "import json;d=json.load(open(r'WorldApart_Data/StreamingAssets/yoo/RawFilePackage/RawFilePackage_1.0.json',encoding='utf-8'));print(d['BundleList'][0])"

# 全量核对 FileCRC/FileSize（1701/1701 通过，CRC 为字节倒序）
python - <<'PY'
import json,zlib,os
ROOT=r"WorldApart_Data/StreamingAssets/yoo/RawFilePackage"
bl=json.load(open(ROOT+"/RawFilePackage_1.0.json",encoding='utf-8'))['BundleList']
rev=sum(1 for e in bl if (lambda c: bytes.fromhex(c)[::-1].hex()==e['FileCRC'])("%08x"%zlib.crc32(open(os.path.join(ROOT,e['FileHash']+'.mp4'),'rb').read())))
print("entries",len(bl),"crc_byte_reversed_match",rev)
PY

# 反汇编（工具在 tools/venv 下，符号取自 il2cpp_out/dump.cs）
python tools/il2dis.py 6600760 200      # DBFSLoadRawBundleOperation.InternalUpdate
python tools/il2dis.py 6618650 200      # FileVerifyHelper.FileVerify
python tools/il2dis.py 6603960 130      # DefaultBuildinFileSystem.LoadBundleFile
python tools/il2dis.py 9DC810 2200      # FsmInitializePackage.<InitRawFilePackage>d__5.MoveNext
# 全段调用者扫描（务必自写；--callers 默认范围不覆盖 YooAsset 所在 RVA）
```
