# DiceVaders 信息面板

> 把游戏里**看不到的数值**实时显示出来。不改数值平衡，只给信息。
>
> 适用游戏：**DiceVaders**（Steam AppID `3917700`）

---

## 两个功能

### 1. 商品棋子概率（贴屏幕左边缘）

游戏里「商店稀有度」只是一个数字，玩家看不出它意味着什么。左列把它换算成**各档位的实际出现概率**：

```
商品棋子
传说 0.0%
稀有 4.5%
罕见 32.5%
普通 63.0%
```

### 2. 神器物品概率（贴屏幕右边缘）

右列显示神器各稀有度的出现概率。**商品与神器走的是同一套抽取逻辑**（`GetRandomRarity`），所以两列数值相同 —— 分开显示只是方便对照游戏里两个不同的东西。

```
神器物品
传说 0.0%
稀有 4.5%
罕见 32.5%
普通 63.0%
```

> **为什么贴左右边缘**：游戏的战斗区在正中间、骰子区在底部中间，
> 左右两侧的边缘空档正好放这两列竖排小字，不挡任何操作。

---

## 安装

### 前置：BepInEx 6（IL2CPP 版）

> ⚠️ **必须是 BepInEx 6，不是 5.x。**

1. 下载 **BepInEx 6 Windows x64 (IL2CPP)**：
   - <https://github.com/BepInEx/BepInEx/releases>（找 `bleeding-edge` 预发布）
   - 或 <https://builds.bepinex.dev/projects/bepinex_be>
   - 验证过的版本：**6.0.0-be.788**

2. 解压出的文件全部放进游戏根目录（能看到 `DiceVaders.exe` 的地方）

3. **启动一次游戏再退出** —— 让 BepInEx 生成运行环境

   > 首次启动会慢一些（它要用 Cpp2IL 扫描 `GameAssembly.dll` 生成 interop 程序集，
   > 日志里会看到 `Detected outdated interop assemblies, will regenerate them now`）。
   > 生成完之后 `BepInEx\interop` 里会出现 100 多个 dll。

### 安装本 mod

**把压缩包解压到游戏根目录**（提示覆盖就确认）。包内已排好结构：

```
DiceVaders\
└── BepInEx\
    └── plugins\
        ├── DiceVaders.ShopInfo.dll
        ├── DiceVaders.ConstellationInfo.dll
        └── DiceVaders.ModKit.dll
```

三个文件都要放（`ModKit` 是两个功能共用的支撑库）。

### 确认装好了

打开 `<游戏根目录>\BepInEx\LogOutput.log`，搜 `ShopInfo`，能看到这些行就说明成功：

```
[Info :   BepInEx] Loading [DiceVaders ShopInfo 1.0.0]
[Info :DiceVaders ShopInfo] ===== DiceVaders ShopInfo v1.0.0 =====
[Info :DiceVaders ShopInfo] ShopInfo: 左右文本已创建
[Info :DiceVaders ShopInfo] [ShopInfo] 权重基数 W 变为 5
```

---

## 配置

```
<游戏根目录>\BepInEx\config\dicevaders.shopinfo.cfg
```

```ini
[1-显示]
ShowPanel = true          # 总开关
OffsetX = 20              # 左列距屏幕【左】边缘像素
RightOffsetX = 460        # 右列距屏幕【右】边缘像素（默认落在「发射！」按钮左边）
OffsetY = 140             # 两列距屏幕【底部】像素
FontSize = 15             # 字号（想更小就 12~13）
ShowArtifactProbs = true  # 右侧那列（神器概率）

[2-调试]
LogOnRarityChange = true  # W 变化时写一行日志，便于核对
```

**位置想调**：

| 想要的效果 | 改哪个 |
|---|---|
| 左列离边缘更远 | `OffsetX` 调大 |
| 右列往左挪 | `RightOffsetX` 调大 |
| 右列往右挪 | `RightOffsetX` 调小 |
| 两列整体上移 | `OffsetY` 调大 |
| 两列整体下移 | `OffsetY` 调小 |
| 字更大/更小 | `FontSize` |
| 只留左边一列 | `ShowArtifactProbs = false` |

---

## 概率是怎么算的（技术说明）

游戏用这个公式决定商店出货档位（`ContentGetter.GetRandomShopEntity`）：

```c
W = MIN((幕数 - 1) × 5, 20) + 商店稀有度        // EncounterModel.GetCurrentShopRarity()

f16 = W × 0.001 - 0.03     // 传说阈值
f15 = W × 0.002 + 0.03     // 稀有增量
f5  = W × 0.015 + 0.1      // 罕见增量

r = Random.NextDouble();
if      (r < f16)             → 传说
else if (r < f16 + f15)       → 稀有
else if (r < f16 + f15 + f5)  → 罕见
else                          → 普通
```

**注意这是累积分布**，所以真实概率要取差值：

```
P(传说) = f16
P(稀有) = (f16+f15) - max(f16,0)
P(罕见) = (f16+f15+f5) - (f16+f15)
P(普通) = 1 - (f16+f15+f5)
```

（把 `f15`/`f5` 直接当概率是错的 —— `W=0` 时真实稀有概率是 **0%**，直接取会得到 3%。）

### 概率速查表

| W | 传说 | 稀有 | 罕见 | 普通 |
|---|---|---|---|---|
| 0 | 0.0% | 0.0% | 10.0% | 90.0% |
| 10 | 0.0% | 3.0% | 25.0% | 72.0% |
| 20 | 0.0% | 6.0% | 40.0% | 54.0% |
| 30 | 0.0% | 9.0% | 55.0% | 36.0% |
| 40 | 1.0% | 11.0% | 70.0% | 18.0% |
| 50 | 2.0% | 13.0% | 85.0% | 0.0% |
| 60 | 3.0% | 15.0% | 82.0% | 0.0% |

**两个临界点**：
- **W = 31** 起传说档才会出现
- **W = 50** 起普通档归零

---

## 常见问题

**Q：面板不显示？**

1. 先确认三个 dll 都在 `BepInEx\plugins\`（不能放子目录）
2. 看日志有没有 `Loading [DiceVaders ShopInfo 1.0.0]`
3. 面板只在**对局内**显示（主菜单/选人界面没有对局数据，会留空）

**Q：文字太小/太大？**

改 cfg 的 `FontSize`（默认 15，可试 12~20）。

**Q：位置不对，挡住东西了？**

改 `OffsetX` / `OffsetY`。`OffsetX` 是距屏幕中点的距离，`OffsetY` 是距屏幕底部的距离。

**Q：数值和游戏对不上？**

`W` 应该等于「（当前幕数-1）×5（上限20）+ 商店稀有度」。如果对不上，把日志发我（里面有 `[ShopInfo] 权重基数 W 变为 N` 的记录）。

**Q：游戏更新后失效？**

IL2CPP 游戏每次更新都可能改变内部结构。失效了就等更新，或按下面的「从源码构建」自己修。

---

## 从源码构建

**前置**：.NET SDK 6.0+，游戏已装 BepInEx（`BepInEx\interop` 里有程序集）。

```bash
git clone https://github.com/HaibaraZAiX/DiceVaders-InfoPanel.git
cd DiceVaders-InfoPanel

# 先编译 ModKit（另外两个要引用它）
dotnet build src/ModKit/DiceVaders.ModKit.csproj -c Release

# 再编译两个功能插件
dotnet build src/ShopInfo/DiceVaders.ShopInfo.csproj -c Release
dotnet build src/ConstellationInfo/DiceVaders.ConstellationInfo.csproj -c Release

# 游戏不在默认位置就加 -p:GameDir="D:\你的路径\DiceVaders"
```

---

## 技术说明（给想改的人）

- **引擎**：Unity 6000.3.x / IL2CPP / 元数据 v39
- **框架**：BepInEx 6 + Il2CppInterop
- **实测发现的 IL2CPP 限制**：注入的 MonoBehaviour 类，**方法签名里不能出现托管类型**
  （`StringBuilder` / `List<T>` 会被 `Il2CppInterop` 拒注册并在日志里警告）——
  所以本项目所有辅助方法的参数/返回值只用 IL2CPP 类型和 `string`/`float`/`int`，
  复杂结果写入实例字段
- **坐标换算**：面板用独立 Overlay Canvas（`sortingOrder=31000`），
  锚点取屏幕底部中点，左右各偏移 `OffsetX`

---

## 许可

MIT License

---

## 免责声明

个人学习逆向工程的产物，与游戏开发商无关。仅供单机离线使用。
