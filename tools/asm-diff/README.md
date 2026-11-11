# asm-diff

验证工具，**不属于产品代码**，也不在 `GlassDesk.slnx` 内，不会被 `build.ps1` 构建。

## 用途

证明「只改了注释，没改代码」。

原始文件哈希做不到这件事：源码位置变化会改写调试用的序列点信息，使程序集字节不同，而逻辑完全一样。本工具比对的是**可执行代码本身**：

- 类型全名
- 方法名与签名哈希
- **每个方法体的 IL 字节哈希**（决定性的一项）
- 字段与属性名
- **全部字符串字面量**（用户可见文字都在这里）

## 用法

```bash
cd tools/asm-diff
"D:\dotnet10\dotnet.exe" build AsmDiff.csproj -c Release

./bin/Release/net10.0/AsmDiff.exe <旧程序集.dll> out/base.txt
./bin/Release/net10.0/AsmDiff.exe <新程序集.dll> out/new.txt
diff out/base.txt out/new.txt
```

`diff` 无输出即表示可执行代码与字符串字面量完全一致。

## 首次使用记录（2026-09-18）

把 1242 行英文注释译为中文后，用它验证 7 个程序集的 IL 与字符串字面量全部逐字节相同，确认翻译没有触碰任何代码。详见 `docs/阶段1-交付报告.md` 第八节。

## 注意

它读取的是**编译产物**，不是源码。要比较两个版本，需要保留旧的程序集（例如发布产物目录的副本）。