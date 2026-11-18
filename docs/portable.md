# 便携版(portable)说明

发布页**只提供 thin 包**(约 0.8 MB),不含 .NET 运行时。这样包小、下载快,装过一次 [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)(x64)之后,以后每个版本都只用下这一份。

如果你需要「换台电脑解压就能用、不装运行时」的版本,自己打一个即可:

```powershell
# 需要 .NET 9 SDK + VS2022 C++ 桌面开发工具(编译原生合并器)
powershell -ExecutionPolicy Bypass -File publish.ps1 -Only portable
```

产物在 `publish\ScreenRec-portable.zip`,约 56 MB,解压双击 `ScreenRec.exe` 即可运行。

## 两者有什么区别

| | thin | portable |
|---|---|---|
| 体积 | 0.8 MB | 56 MB |
| 依赖 | 需要 .NET 9 Desktop Runtime (x64) | 无 |
| 适用 | 自己用、或对方已经装过运行时 | 发给别人、离线环境、不想装运行时 |

两个包内含的可执行文件完全相同(`ScreenRec.exe` + `ScreenRec.Agent.exe` + `ScreenRec.Merger.exe`),功能没有差别。

## 为什么不随发布提供 portable

一是托管平台的单文件发布对 C++/CLI 混合程序集支持不好(`BadImageFormatException`),二是每次发版都要多传 56 MB,而绝大多数人装一次运行时之后就再也用不上了。
