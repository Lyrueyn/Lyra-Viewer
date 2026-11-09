# DanmakuFlameMaster（弹幕大师）源码全解析

> 说明：本文件是我（AI）逐文件通读 `F:\DSH\LocalMediaPlayer-Workspace\app\DanmakuFlameMaster-master\DanmakuFlameMaster-master` 后写的一份讲解文档。
> 面向想搞懂这套代码的开发者，尽量用"大白话 + 少量必要的英文术语（并解释）"来描述。
>
> 项目版权：`Copyright (C) 2013-2015 Chen Hui <calmer91@gmail.com>`，Apache License 2.0。

---

## 一、这个项目是干什么的

`DanmakuFlameMaster`（简称 **DFM**）是 **B 站（Bilibili）开源的 Android 弹幕解析与绘制引擎**。它做一件事：

> 把一段"弹幕数据"（比如哔哩哔哩的 XML、AcFun 的 JSON）解析成一条条弹幕，并在 Android 屏幕上**高效地**把它们画出来，同时支持滚动、固定顶部/底部、高级动效，还有各种"屏蔽/过滤"能力。

它不是一个播放器，而是一个**叠加在视频上面的渲染层**。你可以把它理解成"用在 Android 上的 libass 字幕渲染器"，只不过渲染的对象是弹幕而不是字幕。

几个关键设计定位（来自 README 和源码里的常量）：

- 用多种 View 方式实现高效绘制：普通 `View` / `SurfaceView` / `TextureView`，还支持离屏渲染（`FakeDanmakuView`）。
- 精确还原 B 站 XML 弹幕格式，支持 mode 7（高级/特殊）弹幕（平移、缩放、透明度渐变、旋转、路径运动）。
- **多核机型优化 + 高效的预缓存机制**：这是整个库最精华的部分——把"画文字"这个昂贵操作放到后台线程预先渲成 Bitmap，渲染线程只做"贴图"。
- 实时弹幕支持（直播弹幕 `isLive`）。
- 支持自定义字体、描边、阴影、投影、下划线、边框、图文混排、多行/换行。

---

## 二、仓库目录结构（先看地图）

```
DanmakuFlameMaster-master/
├── build.gradle            根构建脚本（AGP 3.0.1，jcenter+google）
├── settings.gradle         声明 5 个 module
├── gradle.properties       版本号 VERSION_NAME=0.9.25，发布坐标 com.github.ctiao
├── DanmakuFlameMaster/      ★ 核心库（com.android.library，产出 AAR）
│   └── src/main/java/
│       ├── master/flame/danmaku/
│       │   ├── controller/   【控制层】绘制任务、调度器、过滤器、刷新线程
│       │   └── danmaku/
│       │       ├── loader/   【加载层】B站/ACFun 数据加载器（拿原始字节流）
│       │       ├── model/    【模型层】弹幕对象、计时器、集合、抽象显示接口
│       │       │   ├── android/     Android 具体实现（绘制、缓存、工厂、上下文配置）
│       │       │   └── objectpool/  对象池（复用 DrawingCache 的内存）
│       │       ├── parser/   【解析层】把字节流变成弹幕数据集合
│       │       ├── renderer/ 【渲染层】轨道分配、实际绘制调度
│       │       └── util/     工具类（碰撞检测、缓存构建、时钟）
│       └── ui/widget/       【UI 层】四个 View 实现 + 触摸处理
├── Sample/                  demo app（演示怎么用）
├── ndkbitmap-armv5/         NDK 原生模块（armeabi，提供 libndkbitmap.so）
├── ndkbitmap-armv7a/        NDK 原生模块（armeabi-v7a）
└── ndkbitmap-x86/           NDK 原生模块（x86）
```

**模块关系**（`settings.gradle`）：核心库 `DanmakuFlameMaster` + 示例 `Sample` + 三个 NDK 库。NDK 库负责用 native 代码创建 Bitmap，目的主要是**绕过 Java 层 Bitmap 分配的内存开销**（见 `NativeBitmapFactory`）。

> ⚠️ 注意：这里有两层同名目录 `DanmakuFlameMaster-master/DanmakuFlameMaster-master/`，是 GitHub 下载 zip 时常见的"外层包裹目录"。真正项目根是第二层，上面的路径我都以第二层为根。

---

## 三、整体架构：数据是怎么从"文件"变成"屏幕上动的字"的

整个库是个**分层的流水线**，可以简化成一条主线：

```
 原始数据（URL / 文件 / InputStream）
        │
   1. loader【加载层】   BiliDanmakuLoader / AcFunDanmakuLoader
        │      → 产出 IDataSource 数据源（InputStream 或 JSONArray）
        ▼
   2. parser【解析层】   BaseDanmakuParser 子类（如 BiliDanmukuParser / AcFunParser）
        │      → 用 SAX/JSON 解析，逐条用 DanmakuFactory 创建弹幕对象
        │       （parse() 返回一个 IDanmakus 弹幕集合，按时间排序）
        ▼
   3. model【模型层】    BaseDanmaku 及子类（R2L/L2R/FT/FB/Special）
        │      → 每条弹幕是一个对象，记录出现时间、文本、字号、颜色、存活时长等
        ▼
   4. controller【控制层】 DrawHandler（消息调度）→ DrawTask（绘制任务）
        │      → 每帧从集合里取出"当前时间附近"的一批弹幕，交给 renderer
        ▼
   5. renderer【渲染层】  DanmakuRenderer → DanmakusRetainer（轨道分配）
        │      → 每条弹幕量尺寸、过滤、分配合适的"行"（y 坐标），再 draw
        ▼
   6. ui.widget【UI 层】  DanmakuView / SurfaceView / TextureView
        │      → 把 Canvas 交给 renderer，最终把像素画到屏幕上
        ▼
   屏幕上滚动的弹幕
```

关键点：**第 4、5 层是"帧循环"，每帧都跑一遍；而"画文字"这个重活被缓存成 Bitmap，不在帧循环里做**。这是 DFM 高性能的核心。

---

## 四、逐包逐个类详解

### 4.1 model 包 —— 数据与抽象接口

#### `BaseDanmaku.java`（所有弹幕的抽象基类，最核心的模型）

这是理解整个库的钥匙。它定义：

- **弹幕类型常量**（B 站协议编号）：
  - `TYPE_SCROLL_RL = 1`：从右往左滚动（最常见的弹幕）。
  - `TYPE_SCROLL_LR = 6`：从左往右滚动。
  - `TYPE_FIX_TOP = 5`：顶部固定。
  - `TYPE_FIX_BOTTOM = 4`：底部固定。
  - `TYPE_SPECIAL = 7`：高级/特殊弹幕（mode 7，能动、能变色、能缩放）。
  - `TYPE_MOVEABLE_XXX = 0`：预留。
- 一整套"状态刷新"机制：**reset flag（重置标志位）**。这个库用"全局递增的版本号"代替了传统 `boolean isDirty`。因为弹幕对象会被反复复用，用一个会变化的 `GlobalFlagValues`（见下），每个弹幕记住"我在哪个版本号做了 measure / prepare / visible / filter"，版本号一变，就知道"需要重做"。这避免了缓存失效时重复干活，也让复用变得安全。
  - 具体标志位：`measureResetFlag`、`visibleResetFlag`、`filterResetFlag`、`prepareResetFlag`、`syncTimeOffsetResetFlag`、`firstShownFlag`。
- 可视化 / 生命周期方法：
  - `measure()`：量尺寸（写入 `paintWidth/paintHeight`）。
  - `prepare()`：准备绘制（比如图文混排时加载图片）。
  - `draw()`：画到 displayer 上。
  - `layout()`：给定 x/y 后计算矩形。
  - `isShown()` / `isTimeOut()` / `isOutside()` / `isLate()`：判断弹幕当前所处阶段。
- 大量**公开字段**（为了省去 getter/setter 开销和对象创建，直接开字段）：`time`、`text`、`lines`（多行拆好的数组）、`textSize`、`textColor`、`textShadowColor`（描边/阴影色）、`underlineColor`、`borderColor`、`padding`、`priority`（0 低优先级可被过滤，>0 高优先不会被过滤）、`duration`（存活时长）、`tag`（外部自定义数据）、`obj`（库内部用）。
- `getActualTime()` 很关键：返回 `time + timeOffset`。`timeOffset` 用于**外部播放器与弹幕引擎对时**（比如用户拖动了视频进度或做了暂停补偿），见 `AbsDanmakuSync`。

#### `Danmaku.java`（一个"空弹幕"）

继承 BaseDanmaku，所有方法都返回空/默认值，`isShown()` 恒为 false。它的实际用途很隐蔽：在 `Danmakus` 里作为**二分查找的"哨兵/游标"**（`createItem("start")` / `createItem("end")`），用来定位时间区间，本身不渲染。

#### `R2LDanmaku.java`（右→左滚动弹幕）

- `layout()`：根据当前时间算 x 坐标，调 `getAccurateLeft()`。
- `getAccurateLeft()`：**匀速直线运动**。`x = 屏宽 - elapsedTime * mStepX`，其中 `mStepX` 在 `measure()` 里算好：`(屏宽 + 弹幕宽) / duration`。也就是从屏幕最右侧进去，走完"屏宽+弹幕宽"的距离正好是 duration。
- `isOutside()` 等继承自基类。

#### `L2RDanmaku.java`（左→右滚动弹幕）

直接继承 `R2LDanmaku`，只重写了 `getType()`（返回 `TYPE_SCROLL_LR`）和向左移动的 `getAccurateLeft()`。注意它的 `getAccurateLeft`：`x = mStepX * elapsedTime - paintWidth`，即从屏幕左外侧进来。

#### `FTDanmaku.java`（顶部固定弹幕）

- `layout()`：只在弹幕存活期间显示，`y` 由轨道分配器传入，x 水平居中。
- `getLeft()`：`(屏宽 - 弹幕宽) / 2`，并做了缓存（只有屏宽或弹幕宽变了才重算）。
- 位置固定，不动。

#### `FBDanmaku.java`（底部固定弹幕）

最简单，只是继承 `FTDanmaku` 并把 `getType()` 返回 `TYPE_FIX_BOTTOM`。实际对齐逻辑由 renderer 的 `AlignBottomRetainer` 处理。

#### `SpecialDanmaku.java`（高级/特殊弹幕，mode 7）

这个类代码量大，实现"能动的高级弹幕"，能力包括：

- **平移**：`beginX/beginY` → `endX/endY`，`translationDuration`（移动时长）、`translationStartDelay`（延迟启动）。
- **透明度渐变**：`beginAlpha/endAlpha`，`alphaDuration`。
- **旋转**：`rotateX/rotateZ`（配合 AndroidDisplayer 的 Camera/Matrix 实现 3D 翻转）。
- **缩放适配**：`ScaleFactor` 内部类，根据视口大小变化把坐标同比放大/缩小（因为 B 站坐标是相对某个原始分辨率的，比如 682×438）。
- **路径运动**：`LinePath[] linePaths`，一条弹幕可以沿折线走，`setLinePathData()` 会把总时长按路径长度拆分给每一段。
- **缓动**：`isQuadraticEaseOut` 决定用 `Quadratic.easeOut` 而不是线性。
- `getRectAtTime()`：根据当前时间算出当前坐标（考虑平移、路径、缓动、透明度），并决定可见性。这是特殊弹幕的核心计算函数。

> 高级弹幕和普通弹幕在渲染路径上不同：普通弹幕坐标是"轨道分配器"给的固定行 + 匀速公式，特殊弹幕坐标是"随时间算出来的一串数"。

#### 其它 model 基础类

- `IDisplayer.java`：**抽象显示设备接口**。定义 `getWidth/getHeight`、`draw`、`measure`、`prepare`、`setSize`、`setDanmakuStyle` 等。它把"绘制"抽象成接口，方便换成不同实现（Android 实现就是 `AndroidDisplayer`）。还有弹幕样式常量：`DANMAKU_STYLE_NONE/SHADOW/STROKEN/PROJECTION`（无/阴影/描边/投影）。
- `AbsDisplayer.java`：`IDisplayer` 的抽象骨架，补充 `drawDanmaku`（真正把一条弹幕画到 Canvas 的逻辑）、`setTypeFace`、`setCacheStuffer` 等抽象方法。
- `IDanmakus.java`：**弹幕集合接口**。定义了排序方式常量（`ST_BY_TIME` 按时间、`ST_BY_YPOS` 按 y 位置、`ST_BY_LIST` 链表序）、增删、`sub/subnew`（取时间区间子集）、`forEach/forEachSync`（遍历，带一个 `Consumer` 回调）。`Consumer` 是一个"可中断、可删除"的遍历回调，返回 `ACTION_CONTINUE / ACTION_BREAK / ACTION_REMOVE / ACTION_REMOVE_AND_BREAK`。内嵌了三个比较器 `TimeComparator / YPosComparator / YPosDescComparator`。
- `DanmakuTimer.java`：简易计时器，记录 `currMillisecond` 和上一次 `lastInterval()`。
- `Duration.java`：弹幕存活时长，带 `factor`（倍率）和 `value`（实际值）。`setFactor` 用来按滚动速度缩放。
- `AlphaValue.java`：`MAX=255`、`TRANSPARENT=0`，透明度常量。
- `GlobalFlagValues.java`：**全局版本号**。上面说的 reset flag 就靠它。全局只有一个实例，任何配置变化（字号、透明度、可见性等）就 `updateXxx()/updateAll()` 递增对应版本号，所有弹幕只需比较自己记录的版本号就知道要不要重算。这是"批量失效"的聪明做法——不必逐条标记，只改一个全局号。
- `IDrawingCache.java` / `ICacheManager.java`：绘制缓存接口 / 缓存管理器接口。
- `AbsDanmakuSync.java`：**与外部播放器对时的抽象**。提供 `getUptimeMillis`（外部播放时间）、`getSyncState`（播放/暂停状态）、`getThresholdTimeMills`（超过该差值才校正，默认 1500ms）。用来让弹幕时间跟随视频，不漂移。

#### `model/android/` 包 —— Android 具体实现

**`DanmakuContext.java`（全局配置中心，类似一个 Builder）**

这是使用者配置最多的地方。它保存了所有可调参数：

- 字型 `Typeface mFont`、透明度 `transparency`、字号缩放 `scaleTextSize`、边距 `margin`。
- 各类型弹幕可见性：`FT/FB/L2R/R2L/Special` 五组开关。
- 同屏数量上限 `maximumNumsInScreen`（-1 自动，0 无限制，n 固定）。
- 滚动速度系数 `scrollSpeedFactor`。
- 各种黑白名单：颜色白名单、用户 ID / 用户 Hash 黑名单、屏蔽游客。
- 是否合并重复弹幕、是否底对齐、最大行数、是否防重叠。
- 缓存策略 `CachingPolicy`。
- 一个 `DISPLAYER`（`AndroidDisplayer`）、一个 `GLOBAL_FLAG_VALUES`、一个 `FILTERS`、一个 `DanmakuFactory`。

每个 `setXxx()` 都返回 `this`，支持链式调用（典型 Builder 模式）。**几乎所有 setter 都会调用 `notifyConfigureChanged()`**，通过 `ConfigChangedCallback` 告诉 `DrawTask`"配置变了，需要刷新/重建"。

`registerFilter` / `unregisterFilter` 让使用者注册自定义过滤器（黑白名单、去重等），这些过滤器统一由 `DanmakuFilters` 管理。

**`DanmakuFactory.java`（弹幕工厂）**

负责创建不同类型的弹幕对象，并把 **B 站原始分辨率坐标** 换算到当前屏幕。

- 常量：`BILI_PLAYER_WIDTH=682`、`BILI_PLAYER_HEIGHT=438`（B 站播放器原始尺寸）、`COMMON_DANMAKU_DURATION=3800`（弹幕默认存活毫秒）。注意还有个 `OLD_BILI_PLAYER_WIDTH=539`（老版本 B 站，用来兼容历史数据）。
- `createDanmaku(type, ...)`：按类型 new 出对应的弹幕类。
- `updateViewportState()`：每次视口尺寸变化时，重新计算 `REAL_DANMAKU_DURATION`（当前分辨率下的存活时长），并把结果夹在 `MIN_DANMAKU_DURATION=4000` 到 `MAX_DANMAKU_DURATION_HIGH_DENSITY=9000` 之间。
- `fillTranslationData` / `fillAlphaData` / `fillLinePathData`：给特殊弹幕填位移、透明度、路径数据，同时乘以缩放系数把 B 站坐标映射到当前屏。
- MAX_Duration 系列：维护"滚动/固定/特殊"三类弹幕的最大存活时长，用于缓存窗口估算。

**`AndroidDisplayer.java`（真正的绘制者，用 Android Canvas/Paint）**

它把 `IDisplayer` 的抽象方法在 Android 上落地：

- 内部持有 `DisplayerConfig`，里面有 `TextPaint`（主画笔）、`PAINT_DUPLICATE`（副画笔）、`ALPHA_PAINT`、`UNDERLINE_PAINT`、`BORDER_PAINT`，以及描边/阴影/投影等的配置。
- `draw(BaseDanmaku)`：绘制一条弹幕。它先尝试**走缓存**（`sStuffer.drawCache`），如果缓存命中就直接贴 Bitmap；未命中才调 `drawDanmaku` 现画文字（并统计 cacheHit/cacheMiss）。特殊弹幕还处理了透明、旋转（Camera + Matrix）。
- `measure()`：用 Paint 量出文本宽高，再叠加 padding、边框、描边宽度得到 `paintWidth/paintHeight`。
- `setDanmakuStyle()`：切换无/阴影/描边/投影。
- `setSize()`：设置视口宽高，并算出 `locationZ`（用于 3D 旋转的 Camera 位置）。

**`BaseCacheStuffer.java` 及其子类（缓存填充器 / 绘制器）**

这是"缓存"和"现画"两种走法的实现者。抽象方法有 `measure`、`drawDanmaku`、`clearCaches`。

- `SimpleTextCacheStuffer`（默认）：只支持纯文字。`measure()` 用 `paint.measureText`；`drawDanmaku()` 用 `canvas.drawText`，并处理了多行（`lines`）、描边、下划线、边框。
- `SpannedCacheStuffer`：支持 `Spanned` 富文本（图文混排、背景色等）。它用 `StaticLayout` 来排版和绘制富文本，用 `SoftReference` 持有 `StaticLayout` 避免泄漏。
- `ViewCacheStuffer`（抽象）：把"一条弹幕"当成一个 Android `View` 来绘制（复用 `ViewHolder` 模式），适合非常自定义的弹幕外观。Sample 里 `UglyViewCacheStufferSampleActivity` 用了它。

**`DrawingCache.java` / `DrawingCacheHolder.java`（绘制缓存本体）**

- `DrawingCache` 持有一个 `DrawingCacheHolder`，并实现了对象池接口（`Poolable`）——缓存对象本身可以被回收复用，减少 GC。
- `DrawingCacheHolder` 真正持有 `Bitmap` + `Canvas`。`buildCache()` 会优先**复用**已有的尺寸合适的 Bitmap（`reuse` 判断），否则新建。
- `splitWith()`：当硬件加速的 Bitmap 超过单张上限时，把一个大 Bitmap **切分成多块**（`bitmapArray`），避免"超限 Bitmap"画不出来（硬件加速 canvas 对 Bitmap 尺寸有限制）。`draw()` 时按块绘制。
- 用 `NativeBitmapFactory.createBitmap` 通过 NDK 创建 Bitmap，降低分配开销。

**`CachingPolicy.java`（缓存策略）**

几个可调参数：缓存位深（ARGB_4444=16 或 ARGB_8888=32，Android 4.4 起强制 8888）、缓存池占内存的百分比、回收周期、可复用缓存的尺寸容差、强制回收阈值。提供了 `POLICY_LAZY`（省内存，默认）和 `POLICY_GREEDY`（缓存更多）。

**`Danmakus.java`（弹幕集合的 Android 实现）**

- 实现 `IDanmakus`。根据排序类型选容器：`ST_BY_TIME/ST_BY_YPOS` 用 `TreeSet`（带比较器），`ST_BY_LIST` 用 `LinkedList`。
- `sub/subnew`：用两个哨兵 `Danmaku("start"/"end")` 在 `TreeSet` 上 `subSet(start,end)` 做时间区间二分，得到"当前时间窗口"的弹幕。缓存了 `subItems` 避免重复建。
- 遍历用 `forEach`（`Consumer` 支持中断、删除），同步版本 `forEachSync` 加锁。

#### `model/objectpool/` 包 —— 对象池

一套小型的对象池（Android `android.util.Pools` 的简化版）：`Pool`（接口）、`FinitePool`（有限池）、`SynchronizedPool`（线程安全包装）、`Poolable/PoolableManager`（可入池对象/池工厂）。这里专门用于复用 `DrawingCache`（`DrawingCachePoolManager`），因为 Bitmap 内存很贵，反复 new 会引发 GC 抖动。

### 4.2 parser 包 —— 解析层

**`BaseDanmakuParser.java`（抽象解析器基类）**

- `load(IDataSource)`：挂上数据源。
- `setDisplayer`：设置显示设备，并据此更新工厂的视口/存活时长。
- `getDanmakus()`：**懒加载**，首次调用才 `parse()` 并 `releaseDataSource()`（引用用完即释放）。
- 抽象方法 `parse()` 由子类实现。
- `getViewportSizeFactor()`：决定滚动弹幕速度，`1 / (density - 0.6)`。这是 B 站系播放器的经验公式。

**`IDataSource.java`**：数据源接口，`data()` 拿数据，`release()` 释放。常量标记 `http/https/file`。

**`parser/android/AndroidFileSource.java`**：从文件、URI、http 得到 `InputStream` 的数据源。
**`parser/android/JSONSource.java`**：从 JSON 字符串/流/URL 得到 `JSONArray` 的数据源（AcFun 用）。

> 注意：BaseDanmakuParser 本身不包含 B 站/ACFun 的实际解析逻辑。真正的解析器在 **Sample 的 `BiliDanmukuParser.java`** 里（用户要拿到库外实现），它用 SAX 解析 B 站 XML `<d p="...">` 节点，`p` 属性用逗号分隔：时间、类型、字号、颜色、时间戳、弹幕池、用户 hash、弹幕 id。特殊弹幕（type=7）文本是 JSON 数组，里面包含了位移、透明度、旋转、路径等。

设计意图：**DFM 库只提供"基类 + 工具"，具体解析格式由使用者实现**，这样不同平台（B 站、AcFun、别家）都能复用同一个渲染引擎。

### 4.3 loader 包 —— 加载层

**`ILoader.java`**：加载器接口，`load(uri)` / `load(InputStream)`，`getDataSource()` 返回数据源。

- `BiliDanmakuLoader`：单例，把 uri 包成 `AndroidFileSource`。
- `AcFunDanmakuLoader`：单例（双重检查锁），把 uri 包成 `JSONSource`。
- `DanmakuLoaderFactory`：`create(tag)` 按 "bili"/"acfun" 返回对应加载器。

### 4.4 renderer 包 —— 渲染层

**`IRenderer.java`（绘制接口）**

- `draw(disp, danmakus, startRenderTime, state)`：绘制一批弹幕。
- `RenderingState`：渲染状态统计类。记录每一帧画了多少、各类型数量、cacheHit/cacheMiss、本帧耗时 `consumingTime`、`indexInScreen`（滚动弹幕已被画了多少条，用于密度过滤）等。`appendToRunningDanmakus` 记录"当前还在屏上"的弹幕，给对时/同步用。

**`Renderer.java`**：`IRenderer` 的空抽象类。

**`DanmakuRenderer.java`（Android 实现，核心帧逻辑）**

`draw()` 遍历弹幕集合，对每条弹幕做一整套流水线，对应 `Consumer.accept()`：

1. `isTimeOut()`？是 → recycle 并删除。
2. `hasPassedFilter()`？否 → 跑一次 `mDanmakuFilters.filter()`。
3. 时间还没到（`getActualTime() < startRenderTime`）或被过滤（`priority==0 && isFiltered()`）→ 跳过。
4. `isLate()`（还没到该显示的时间）→ 加入缓存管理器准备，`ACTION_BREAK`（因为集合按时间排序，后面的更晚，直接停）。
5. 如果是滚动弹幕 → `indexInScreen++`（统计 density）。
6. 没 measure → measure；没 prepare → prepare。
7. `mDanmakusRetainer.fix()`：**关键一步**，调用轨道分配器决定这条弹幕放哪一行、要不要显示。
8. `isShown()`？是 → 真正 `draw()`（先走缓存，未命中才现画），更新状态统计，回调 `onDanmakuShown`。
9. 若 `getBottom() > 屏高`（画到屏幕外了）→ 跳过。

**`DanmakusRetainer.java`（弹幕轨道分配器，非常核心）**

它决定"这条弹幕放在哪一行（y 坐标）"，避免互相重叠。内部为不同弹幕类型准备了不同的分配策略（`IDanmakusRetainer`）：

- 滚动弹幕（R2L/L2R）：`AlignTopRetainer`（默认顶部对齐）或 `AlignBottomRetainer`（底部对齐）。
- 顶部固定：`FTDanmakusRetainer`。
- 底部固定：`AlignBottomRetainer`。
- 特殊弹幕：不参与轨道，`layout(0,0)` 直接按自身公式算。

以 `AlignTopRetainer.fix()` 为例。**它有两个关键概念，别混淆**：

- **`mVisibleDanmakus`（占位集合，用于分配）**：按 y 排序，每个 y 只记"最新占位的一条"。新弹幕插入时 `removeItem(旧占位)` 再 `addItem(新)`——这只是分配用的记账本，**删掉的旧弹幕还在画面上滚**（还没飞完，没从渲染列表移除）。
- **实际画在屏上的所有弹幕（渲染对象）**：这一层**同一条 y 可以同时飞多条**。

`fix()` 分配"放哪一行"的流程大致是：

1. 遍历"当前在屏弹幕"（`mVisibleDanmakus`，按 y 排序）作为候选行。
2. 对每条已显示弹幕，用 `DanmakuUtils.willHitInDuration()` 判断"新弹幕放这一行，飞行过程中会不会追尾撞上"。
3. `willHit`（会撞）→ 往下换一行；`!willHit`（不撞）→ 插在这一行（复用该 y，**同一条 lane 首尾相接，形成一整条"滚动行"**）。
4. 用一个 `minRightRow` 记录"右边最靠左"的行，用于"覆盖式插入"和判断是否越界。
5. `isOutVerticalEdge()`：如果顶到屏幕底部（装不下了），就 `clear()` **占位集合**并强制从顶部重新开始（刷新/重排），**绝不删正在滚的弹幕本体**。

> 一句话：`willHitInDuration` 只对**同类型滚动弹幕**做"会不会追尾"的预测；`type1 != type2`（不同类型）直接返回"不撞"——所以滚动弹幕互相穿过、滚动和顶部/底部固定弹幕叠在一起，都是**允许的、正常现象**。这个分配器权衡的是：**同一条 lane 能首尾相接就接成一条滚动行；真的装不下才 clear 占位重排**——它既不"一行只一条"，也从不主动删屏上的弹幕。

`Verifier` 接口：在 `fix()` 前后提供二次过滤机会（最大行数、防重叠等）。`DanmakuRenderer` 里实现了 `verifier`，用来做 `filterSecondary`（`MaximumLinesFilter`、`OverlappingFilter`）。

### 4.5 controller 包 —— 控制与调度

**`IDrawTask.java`**：绘制任务接口，定义 `addDanmaku/removeAllDanmakus/draw/reset/seek/start/quit/prepare` 等。还定义了`TaskListener`（ready / onDanmakuAdd / onDanmakuShown / onDanmakuConfigChanged / onDanmakusDrawingFinished 五类回调）。

**`DrawTask.java`（绘制任务基类，帧循环主体）**

- 持有 `mRenderer`（DanmakuRenderer）、`danmakuList`（全集）、`danmakus`（当前窗口）、`mTimer`、`mRenderingState`。
- `drawDanmakus()`（由 DrawHandler 每帧调用）：
  1. 清空画布。
  2. 计算时间窗口：`[timer - MAX_DURATION - 100, timer + MAX_DURATION]`，用 `danmakuList.sub()` 拿当前要画的子集，并缓存 `mLastBeginMills/mLastEndMills`（时间没跨窗口就不重取）。
  3. **先画"正在跑的弹幕"（runningDanmakus）**——这是为了对时/同步时，上一帧还在屏上的弹幕能连续地接着动，而不是闪掉重来。
  4. 再画当前窗口的新弹幕 `screenDanmakus`。
  5. `beginTracing/endTracing` 填充渲染状态（耗时、count、缓存命中率）。
- `addDanmaku()`：实时弹幕（`isLive`）走 `mLiveDanmakus`，普通弹幕进 `danmakuList`，并做时间窗口判断。
- `seek()`：跳转（有 `mStartRenderTime` 和复位逻辑）。
- `requestSync()`：对外部播放对时（`AbsDanmakuSync`），给正在跑的弹幕设置 `timeOffset`。
- 配置变化回调：`onDanmakuConfigChanged` → 按具体 tag 处理（字号/速度/边距 → 请求重建 retainer；最大行数/防重叠 → 更新 verifier；等）。

**`CacheManagingDrawTask.java`（带后台缓存管理的绘制任务，性能核心）**

继承 DrawTask，增加了**后台线程预渲染缓存**：

- 启动一个 `HandlerThread "DFM Cache-Building Thread"`（`CacheManager` + `CacheHandler`）。
- 这个后台线程**提前**把即将出现的弹幕 measure、prepare、并**画成 Bitmap 缓存**，这样 UI 帧循环里渲染时直接贴图，不用现画文字。
- `CacheManager` 关键逻辑：
  - `prepareCaches()`：拿未来 `MAX_DURATION * screenSize`（默认 3 屏）窗口内的弹幕，逐条 `buildCache()`。有节奏地 sleep 以避免抢占 UI。
  - `buildCache()`：先从池里找可复用缓存（`findReusableCache`，先严格匹配再宽松匹配）；找不到就新建；超尺寸/超内存就不缓存（返回 RESULT_FAILED，走"现画"路径）。
  - `findReusableCache()`：严格模式要求文本、颜色、尺寸全等；宽松模式允许尺寸在容差范围内的过期/无引用缓存复用（`reusableOffsetPixel` 容差）。
  - 内存管理：`mMaxSize` = 最大内存 × `maxCachePoolSizeFactorPercentage`；缓存 `mRealSize` 超过就 `entryRemoved`（真正 destroy 或放回对象池）。`clearTimeOutCaches` 周期性清理过期缓存。
  - 各种 Handler 消息：`ADD_DANMAKU / BUILD_CACHES / CLEAR_TIMEOUT_CACHES / SEEK / CLEAR_ALL_CACHES / REBUILD_CACHE` 等。
  - `onPlayStateChanged`：暂停时不建缓存，节省 CPU。
- 最后 `onDanmakuConfigChanged` 里，对不同配置 tag 做缓存重建/清理策略。

> 一句话总结：**`CacheManagingDrawTask` 把"贵的字"提前画成"便宜的图"存起来，渲染时只贴图。** 这是 DFM 高性能、省电、流畅的根本。

**`DrawHandler.java`（消息驱动的总调度器）**

继承 `Handler`，跑在一个独立 `HandlerThread` 的 Looper 上（或主线程）。通过 Message 控制整个生命周期：

- `PREPARE / START / UPDATE / RESUME / PAUSE / QUIT / SEEK_POS / SHOW_DANMAKUS / HIDE_DANMAKUS / NOTIFY_RENDERING / FORCE_RENDER` 等消息。
- `updateMethod` 选择刷新方式（`DanmakuContext` 里配置）：
  - `0`：Choreographer 驱动（跟屏幕 vsync 对齐，最流畅）。
  - `1`："DFM Update"单独线程自驱动。
  - `2`：DrawHandler 线程自驱动（老设备回退）。
- `syncTimer()`：**主循环的节拍控制器**。它在真实时间和"弹幕自己的时钟"之间做平滑：如果渲染耗时多（`consumingTime > cordonTime` 或平均耗时大），它就"放快"时钟追赶；否则按稳定节奏 `mFrameUpdateRate`（默认 16ms，约 60fps）前进。`mCordonTime` 是最长等待，`gapTime` 是累积的落后量。这一层保证了**弹幕速度稳定不会忽快忽慢**。
- `draw(Canvas)`：每帧的实际入口，会先处理 `AbsDanmakuSync` 对时，再把 Canvas 交给 `drawTask.draw()`，最后记录渲染时间（`recordRenderingTime`，用于算平均耗时节拍）。
- `getCurrentTime()`：给外层提供"当前弹幕时间"。

**`DanmakuFilters.java`（弹幕过滤系统）**

一系列过滤器，决定"这条弹幕要不要显示"。分为 primary（主）和 secondary（次）两组。内置：

- `TypeDanmakuFilter`：按类型过滤（屏蔽某类弹幕）。
- `QuantityDanmakuFilter`：同屏数量过滤（只对滚动弹幕），超过上限的就跳过。
- `ElapsedTimeFilter`：绘制耗时过滤——如果本帧已经画了超过一定时间（默认 20ms），就跳过后面的，保证流畅。
- `TextColorFilter`：颜色白名单（只有白名单里的颜色才显示）。
- `UserIdFilter` / `UserHashFilter`：用户 ID / hash 黑名单。
- `GuestFilter`：屏蔽游客弹幕。
- `DuplicateMergingFilter`：去重——相同文本的重复弹幕只留一条。
- `MaximumLinesFilter`：最大行数（secondary，配合轨道分配器）。
- `OverlappingFilter`：防重叠（secondary，`willHit` 为真时屏蔽）。

每个过滤器有 `filter()`（返回是否该屏蔽）和 `setData()`（设置数据）。所有过滤器用 `mFilterParam` 的位或叠加标记，且依赖 `GlobalFlagValues.FILTER_RESET_FLAG` 判断是否"本版本号已过滤过"，避免重复过滤。

> 过滤分两个阶段：**primary** 在 `DanmakuRenderer` 里对每条弹幕跑；**secondary** 在 `DanmakusRetainer` 的 `Verifier` 里跑（只有那些"涉及行数/重叠"的才有意义）。

**`DrawHelper.java`**：画布清理工具（用 PorterDuff CLEAR 清透明）和画 FPS 文字。

**`UpdateThread.java`**：一个简单的退出可调用线程，用于"单独线程刷新"模式。

**`IDanmakuView.java` / `IDanmakuViewController.java`**：两个接口。
- `IDanmakuView`：对外暴露的 View 接口（prepare/start/pause/resume/seekTo/release/addDanmaku/回调设置/点击监听等）。
- `IDanmakuViewController`：**内部控制接口**（`isViewReady/getViewWidth/drawDanmakus/clear/isHardwareAccelerated`），供 `DrawHandler` 回调，文档里明确写着 "DO NOT ACCESS"（给内部用）。

### 4.6 ui/widget 包 —— 四个 View 实现 + 触摸

DFM 提供四种渲染载体，都实现了 `IDanmakuView` + `IDanmakuViewController`，可互相替换：

**`DanmakuView.java`（普通 View）**

- 继承 `View`，用 `onDraw(Canvas)` 画。用 `lockCanvas/unlockCanvasAndPost`（内部其实是 `postInvalidateOnAnimation` + 一个 moniter 等待）协调"画布被 UI 线程占用"的问题。
- `drawDanmakus()`：调用 handler.draw。
- 触摸 → `DanmakuTouchHelper`。

**`DanmakuSurfaceView.java`（SurfaceView）**

- 继承 `SurfaceView`，`surfaceCreated/Destroyed` 里管理 surface 生命周期，`drawDanmakus()` 用 `mSurfaceHolder.lockCanvas()` 拿 canvas 再画。SurfaceView 在独立 surface 上绘制，适合作为视频下方的覆盖层（`setZOrderMediaOverlay(true)`）。

**`DanmakuTextureView.java`（TextureView）**

- 继承 `TextureView`，用 `lockCanvas/unlockCanvasAndPost`，`SurfaceTextureListener` 生命周期。TextureView 作为 View 混在普通 View 层级里，支持透明、可做变换。
- 注释明确说目前用 lockCanvas，没用 OpenGL 硬件加速。

**`FakeDanmakuView.java`（离屏渲染 View，特别有用）**

- 继承 `DanmakuView`，但不真正上屏，而是**渲到一张 Bitmap** 里（`initBufferCanvas`）。
- 用途：**批量提前把弹幕画成一张张帧图**（比如录制、生成弹幕预览、给弹幕做"缩略图帧"）。通过 `getFrameAtTime(frameRate)` 和 `OnFrameAvailableListener.onFrameAvailable` 一帧一帧回调。
- 内部用 `CustomParser` 只取 `[stTime, edTime]` 区间的弹幕，并克隆一个干净的 `DanmakuContext`（`resetContext`、关同步、unregister 回调），`updateMethod=1` 强制单线程。

**`DanmakuTouchHelper.java`**：把触摸事件翻译成"点到了哪条弹幕"。

- 点击命中的弹幕会收集到一个 `IDanmakus`（`touchHitDanmaku`），点到的所有弹幕都返回（用一个 RectF 和每条弹幕的矩形求交集）。
- 触发 `OnDanmakuClickListener.onDanmakuClick / onDanmakuLongClick / onViewClick`。

### 4.7 util 包 —— 工具

**`DanmakuUtils.java`**：

- `willHitInDuration()`：判断两条弹幕在存活期间会不会"追尾撞上"（碰撞检测）。**关键规则：`if (type1 != type2) return false;`——不同类型弹幕直接判"不撞"**（所以滚动弹幕互相穿过、滚动与固定弹幕叠放都是允许的）。只对**同类型的滚动弹幕**做"新弹幕左缘 < 旧弹幕右缘"的边界比较；同类型的固定弹幕（顶/底）判为必然占同一行。这是轨道分配器的核心依赖。
- `buildDanmakuDrawingCache()`：把一条弹幕画到缓存 Bitmap 里（`drawDanmaku(..., true)` 在缓存线程绘制），硬件加速时再 `splitWith` 分块。
- `getCacheSize()`：按宽高×字节数估算缓存大小。
- `isDuplicate()` / `compare()`：去重判定、排序比较（先比时间，再比 index）。
- `fillText()`：填弹幕文本，并按 `"/n"`（注意是字母 n，不是 `\n`，这是 B 站协议）拆分多行。

**`IOUtils.java`**：关闭流、读全字节流。

**`SystemClock.java`**：封装 `elapsedRealtime()` 和 `sleep()`。

### 4.8 JNI（tv/cjump）—— NDK Bitmap 工厂

**`NativeBitmapFactory.java`**：通过 NDK 创建 Bitmap 的 JNI 包装。

- `loadLibs()`：加载 `libndkbitmap.so`（只在 `API 11~22`），并做 `testLib` 自检。
- 用反射拿到 `Bitmap.Config.nativeInt` 字段，绕开 Java 创建 Bitmap，直接在高性能的 native 层分配，减少 GC、加快缓存分配。
- 关键：`createBitmap()` 在 native 库可用时走 native，否则回退到 `Bitmap.createBitmap`。

**`DeviceUtils.java`**：检测 CPU 架构（读 `libc.so` 的 ELF machine 字段判断 ARM/X86/MIPS/ARM64），用于判断能不能加载 native 库、以及是否是真机还是模拟器（x86 模拟 ARM）。

> 为什么需要 NDK 版 Bitmap？因为弹幕缓存会频繁创建大量 Bitmap，Java 层创建会频繁触发 GC 与内存抖动，native 分配能跑得更稳、更快。这也是"多核/老机型也能流畅"的一个底气。

---

## 五、核心机制专题（把架构串起来）

### 5.1 帧循环是怎么转的

```
DrawHandler handleMessage(UPDATE)
   └─ syncTimer()  —— 平滑推进弹幕时钟（稳定 60fps 节奏）
   └─ mDanmakuView.drawDanmakus()
        └─ handler.draw(canvas)   （DrawHandler.draw）
             ├─ 处理 AbsDanmakuSync 对时（可选）
             ├─ mDisp.setExtraData(canvas)
             └─ drawTask.draw(mDisp)
                  └─ drawDanmakus()
                       ├─ 清空画布
                       ├─ 取时间窗口子集
                       ├─ 画 runningDanmakus（上一帧还在屏上的，连续）
                       ├─ 画 screenDanmakus（当前窗口新弹幕）
                       └─ 记录渲染状态（耗时、数量、缓存命中）
```

刷新方式有 3 种（`updateMethod`）：Choreographer（vsync 对齐，推荐）、独立线程、自驱动。根据设备性能自动选或手动配。

### 5.2 "行"是怎么分配的（弹幕不打架的密码）

每一种弹幕类型对应一种 `IDanmakusRetainer`。以最常见的滚动弹幕（顶部对齐）为例，`fix()` 的过程就是：

> 拿着新弹幕，扫一遍"现在屏幕上正在飞的弹幕"，逐行问：`willHitInDuration()` 逃得过吗？**逃得过（不撞）就放进这一行**，和原有的弹幕**首尾相接成一条滚动行**；逃不过（会追尾）就往下看下一行。如果一路都逃不过、顶到屏幕底部，就表明这一屏装不下了。

这里要特别说清三个容易误解的点：

- **同一条 lane 不是"只一条"，而是一串"时间前后相接、不互相追尾"的弹幕接龙。** 因为 `mVisibleDanmakus` 分配时只记"最新占位"，但画面层允许旧的还在滚、新的接进来，所以一条滚动行是 N 条按出生时间顺序排队的弹幕。
- **"首尾相接"真正的来源是速度公式：`stepX = (屏宽 + 弹幕宽) / duration`，速度取决于弹幕宽度。** 两条**宽度相同**的弹幕 → 速度相同 → 间距恒定，才是真的匀速接龙（B 站大量同文本刷屏天然如此）；宽度不同 → 速度不同 → 间距会单调变化，B 站靠 `willHit` 的两个端点时刻近似预测"追不追得上"，追不上才放同一条 lane。
- **`willHitInDuration` 里 `if (type1 != type2) return false;`——不同类型之间不做碰撞判定。** 所以 mode1（右→左）和 mode6（左→右）会互相穿过、滚动弹幕和顶部/底部固定弹幕会叠在一起，这些是**设计上允许的重叠**，不是 bug。

- `willHit` 为 true 时还会触发 "覆盖插入"（overwrite）——用新弹幕顶替旧的占位，保证屏幕不会一直空着（但被覆盖的旧弹幕仍在画面上滚到自然离场）。
- 越界检查：如果新弹幕的 y + 高超出屏幕，就 `clear()` 整个 *占位集合*（`mVisibleDanmakus`）并强制从顶部重新开始——注意它 **只清轨道分配器，绝不清正在飞的弹幕本体**（弹幕本体靠 `isOutside`/`elapsed>=duration` 自然离场）。

固定弹幕（顶/底）的分配更简单：同类型的固定弹幕 `willHit` 判为"必然撞"，所以用独立的 `FTDanmakusRetainer` / `AlignBottomRetainer` 各自分配顶/底轨道；不同类的固定弹幕与滚动弹幕之间则允许交叉。

### 5.3 缓存体系（性能的灵魂）

两套缓存协同：

1. **绘制结果缓存（DrawingCache/Bitmap）**：把"一条弹幕的文字"预先画成 Bitmap。
   - 由 `CacheManagingDrawTask` 的后台线程提前做（提前 3 屏的量）。
   - 渲染时 `draw()` 先查缓存，命中直接贴图（`CACHE_RENDERING`），未命中才现画（`TEXT_RENDERING`）并顺手存缓存。
   - 缓存对象用**对象池**复用；Bitmap 用 **NDK** 创建；超限大图**分块**；内存超限**无引用即回收**。
2. **时间窗口子集缓存（Danmakus.sub）**：只取当前时间附近的弹幕来画，避免每帧遍历全集。

`RenderingState.cacheHitCount / cacheMissCount` 实时统计命中率，Sample 里可以显示在 FPS 条上。

### 5.4 过滤体系

过滤器在两处登场：

- **primary**（`DanmakuRenderer` 对每条弹幕）：类型、数量、耗时、颜色白名单、用户黑白名单、游客、去重。
- **secondary**（`DanmakusRetainer.Verifier`）：最大行数、防重叠——这两项必须知道"分配了几行/会不会撞"才能判，所以放在轨道分配之后。

每条弹幕用 `mFilterParam`（位或）记录被哪些过滤器屏蔽过，配合 `GlobalFlagValues.FILTER_RESET_FLAG` 判断"这个版本号过滤过了没"，避免重复过滤。

### 5.5 全局失效号（GlobalFlagValues）

这是 DFM 的一个精巧设计。任何"影响所有弹幕"的配置变化（字号、透明度、边距、可见性开关、滚动速度、去重……），只递增对应版本号：

```
mGlobalFlagValues.updateMeasureFlag();  // 所有人重新 measure
mGlobalFlagValues.updateVisibleFlag();  // 所有人重新判可见性
```

每个弹幕记着自己"上次在哪个版本号做的"，版本号不一致就重做。**高效、无锁、无逐条遍历标记**。

### 5.6 与外部播放器的对时（AbsDanmakuSync / requestSync）

弹幕引擎自己有 `DanmakuTimer`。但视频播放器（如 `VideoView`）有自己的进度。为了两者同步：

- `DanmakuContext.setDanmakuSync(AbsDanmakuSync)` 注入一个"外部时间源"。
- `DrawHandler.draw()` 每帧读外部 `getUptimeMillis()` 和 `getSyncState()`，若与弹幕时钟偏差超过阈值（默认 1500ms），就 `drawTask.requestSync()` 给"正在跑的弹幕"加/减 `timeOffset`，让它们平滑移动到正确时间，而不是闪跳。
- 暂停/恢复也通过 `getSyncState()` 联动。

---

## 六、怎么用（基于 Sample）

使用套路高度统一，SAMPLE 的 `MainActivity` 展示得很清楚：

```java
// 1. 拿到 View（三种之一）
IDanmakuView danmakuView = (IDanmakuView) findViewById(R.id.sv_danmaku);

// 2. 配置 DanmakuContext（链式）
DanmakuContext context = DanmakuContext.create();
context.setDanmakuStyle(IDisplayer.DANMAKU_STYLE_STROKEN, 3)   // 描边
       .setDuplicateMergingEnabled(false)
       .setScrollSpeedFactor(1.2f)
       .setScaleTextSize(1.2f)
       .setCacheStuffer(new SpannedCacheStuffer(), proxy)      // 图文混排
       .setMaximumLines(maxLinesPair)
       .preventOverlapping(overlappingEnablePair)
       .setDanmakuMargin(40);

// 3. 先从资源/文件创建 parser（B 站 XML）
BaseDanmakuParser parser = createParser(stream);

// 4. prepare + start
danmakuView.setCallback(callback);
danmakuView.setOnDanmakuClickListener(...);
danmakuView.prepare(parser, context);
danmakuView.showFPS(true);
danmakuView.start();
```

实时弹幕：`context.mDanmakuFactory.createDanmaku(type)` 造一条，`isLive=true`，`setTime(当前时间)`，`addDanmaku(danmaku)`。

释放：`onDestroy` 里 `danmakuView.release()`。

---

## 七、性能设计总结（为什么它流畅）

1. **后台预渲染缓存**：文字先画成 Bitmap，渲染只贴图。
2. **对象池 + NDK Bitmap**：减少 GC、内存抖动。
3. **超限分块**：绕开硬件加速的单张 Bitmap 上限。
4. **时间窗口子集 + 哨兵二分**：每帧只处理当前附近弹幕。
5. **全局失效号（GlobalFlagValues）**：配置变更 O(1) 批量失效。
6. **自适应节拍（syncTimer）**：渲染慢就快进时钟追，渲染快就按 60fps 走，速度稳定不抖动。
7. **Choreographer 刷新**：跟 vsync 对齐，渲染平滑。
8. **多层过滤提前丢弃**：屏蔽的、超时的、画到屏外的都不进绘制管线。

---

## 八、代码里的一些注意点 / 边界（阅读时的发现）

- 刷新方式 `updateMethod` 在 `Android 4.1 (JELLY_BEAN)` 以下会强制从 0 变 2（Choreographer 不可用）。
- 缓存位深在 Android 4.4 (KITKAT) 起强制用 ARGB_8888（`CachingPolicy` 注释说明了）。
- `SpecialDanmaku` 的 `getRectAtTime` 里 `linePaths.length == 0` 时 `linePaths[j]` 可能越界，但解析器保证了至少 2 个点才建 path，实际风险低。
- `AndroidDisplayer` 的 `draw()` 里 `getTop/getLeft` 在特殊弹幕下依赖 `getRectAtTime` 先算好坐标，顺序敏感。
- 特殊弹幕在 `FakeDanmakuView.CustomParser` 里被 `FIXME` 跳过了（`return 0; // FIXME skip special danmakus`），即离屏渲染批次**不处理模式 7**。
- 缓存构建线程依赖 `mTick`、`mCacheTimer` 与渲染线程的 `mTimer` 配合，`prepareCaches` 里 `repositioned` 判定和 `sleepTime` 是为了不抢占 UI / 不重复建。
- `DanmakuView`（普通 View）没有真正的 surface，它用 `postInvalidateOnAnimation` + wait/notify 的 lockCanvas 机制；`SurfaceView/TextureView` 用 `lockCanvas/unlockCanvasAndPost`。三者实现细节不同但对外接口一致。

---

## 九、和你自研播放器（本地播放器）的关系

你在做的 `local-media-player` 是自己用 `WASAPI/Vulkan/D3D11` 做低层渲染播放器的那个项目，弹幕层最开始也是参考了这个库的思路（还有 libass）。这里补充几点对照，方便你参考其设计但不必照抄（因为你的弹幕层走的是 D3D11 + FreeType 光栅化，性能和架构完全另一回事）：

- DFM 的"**把文字预渲染成位图、渲染时只贴图**"思想，和你弹幕层"FreeType 光栅化到纹理"是同一个思路（都是把文字转成纹理/位图再合成）。
- DFM 的"**轨道分配器避免重叠**"逻辑，可以直接借鉴到你这边的弹幕排布（你 DSP 上那条 `dmTex` 弹幕纹理 + 视口宽/纹理宽不匹配导致横向拉伸的坑，说明你更关心的是**纹理与视口的缩放对应关系**，这在 DFM 里就是 `DanmakuFactory` 的 `BILI_PLAYER_WIDTH` 换算 + `viewPortScale`）。
- DFM 的"**Cheap 的位图缓存 + 命中复用**"很成熟，如果你弹幕量大，可以借鉴"按文本内容做缓存 key"的策略。
- 但 DFM 是 **Android Canvas + Java**，性能上限较低；你的方案是**原生 GPU + 纹理**，是更底层、可控度更高的路线，符合你"选最底层、可控"的偏好。

> 结论：这个库的**算法与架构思路**（缓存、轨道分配、对时、全局失效号）很值得借鉴；它的**具体实现**（Java + Android Canvas）不适合直接用到你的原生播放器里。README 里的 TODO 也写明"未来增加 OpenGL ES 绘制方式"，说明原作者也意识到纯 Canvas 的局限。

---

## 十、License & 开源信息

- License：Apache License 2.0。
- 作者：Chen Hui `<calmer91@gmail.com>`。
- 发布坐标（Maven）：`com.github.ctiao:DanmakuFlameMaster:0.9.25`（`jcenter`），NDK 库 `com.github.ctiao:ndkbitmap-armv7a:0.9.21` 等。
- 官方主页：https://github.com/ctiao/DanmakuFlameMaster
- NDK 源码：https://github.com/Bilibili/NativeBitmapFactory

---

## 附：核心类速查表

| 类 | 作用 | 一句话 |
|---|---|---|
| `BaseDanmaku` | 弹幕抽象基类 | 一条弹幕的数据 + 生命周期 |
| `R2L/L2R/FT/FB/SpecialDanmaku` | 五种子类 | 五种运动/显示形态 |
| `DanmakuFactory` | 工厂 | 按类型建弹幕、B站坐标换屏 |
| `DanmakuContext` | 全局配置 | 所有可调参数 + 回调 |
| `AndroidDisplayer` | Android 绘制实现 | 用 Canvas/Paint 真的画出来 |
| `BaseCacheStuffer` 及其子类 | 缓存填充器 | 画文字 / 富文本 / 自定义 View |
| `DrawTask` / `CacheManagingDrawTask` | 绘制任务 | 帧循环主体 / 带后台缓存 |
| `DrawHandler` | 消息调度 | 生命周期 + 节拍控制 |
| `DanmakuRenderer` | 渲染器 | 每帧逐条弹幕流水线 |
| `DanmakusRetainer` | 轨道分配器 | 决定每条放哪一行 |
| `DanmakuFilters` | 过滤器 | 决定哪些不显示 |
| `DanmakuView/SurfaceView/TextureView` | UI 载体 | 三种上屏方式 |
| `FakeDanmakuView` | 离屏渲染 | 渲到 Bitmap（录制/预览） |
| `DanmakuTouchHelper` | 触摸 | 点中哪条弹幕 |
| `GlobalFlagValues` | 全局失效号 | 配置变化批量失效 |
| `NativeBitmapFactory` | NDK Bitmap | native 建图，降 GC |
