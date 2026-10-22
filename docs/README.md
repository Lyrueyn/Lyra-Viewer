# 本地歌单器 (Local Media Player)

C++(Qt6) + Python 的本地媒体库：本地音频歌单、B站视频下载（弹幕/评论）、本地 AI 画质音质增强。

- **方案设计文档**：见 [docs/方案设计.md](docs/方案设计.md)
- **当前状态**：P0 音频 ✅ / P1 B站（下载480p+弹幕+评论，零依赖）✅ / P2 增强（响度统一已真实验证）✅ / P3（播放模式/最近播放/仿B站首页）✅ —— **核心功能全部零依赖且真实验证**；高清下载（yt-dlp）与 AI 模型增强（降噪/超分/画质）为可选增强项

## 技术栈

| 层 | 技术 |
|---|---|
| 主程序 | C++17 / Qt 6（QML 界面）/ libmpv（播放器，可选） |
| 后端 | Python 3.10 子进程（127.0.0.1:8765），**纯标准库 http.server**（零第三方依赖可运行） |
| 下载 | 纯标准库（B站 playurl + DASH + ffmpeg 合并，免登录 480p，**已真实验证**）；yt-dlp 可选（更高清晰度） |
| AI 增强 | Real-ESRGAN（画质）/ DeepFilterNet（降噪）/ R128（响度）/ AudioSR→FiPA-SR（音频超分） |
| 数据库 | SQLite（backend/data/local_media.db） |

## 目录结构

```
src/             C++ 源码（main.cpp、BackendManager、ui/Main.qml）
backend/         Python 后端（app/ 下分模块：api、db、downloader、bilibili、ai_pipeline、enhancers）
docs/            方案设计文档
```

## 快速开始（工具链已齐）

工具链实测现状：Qt 6.11.1（E:\Qt，含 MinGW 13.10 编译器 / CMake / Ninja）、
Python 3.10、ffmpeg 本机已齐，直接构建即可。详见 [docs/方案设计.md](docs/方案设计.md) 第 6 节。

```bash
# 1. 后端（可选！弹幕/评论/服务全部零依赖，装了 yt-dlp 才能下载视频文件）
cd backend
py -3.10 -m venv .venv
.venv\Scripts\activate
pip install yt-dlp            # 仅下载功能需要

# 2. 手动测试后端
python run_backend.py            # 另开终端 curl http://127.0.0.1:8765/health

# 3. 构建 C++ 主程序（MinGW 路线）
cd ..
cmake -S . -B build -G Ninja -DCMAKE_MAKE_PROGRAM=E:/Qt/Tools/Ninja/ninja.exe -DCMAKE_PREFIX_PATH=E:/Qt/6.11.1/mingw_64 -DCMAKE_C_COMPILER=E:/Qt/Tools/mingw1310_64/bin/gcc.exe -DCMAKE_CXX_COMPILER=E:/Qt/Tools/mingw1310_64/bin/g++.exe -DCMAKE_BUILD_TYPE=Debug
cmake --build build
```

## 路线图

- P0：音频导入 / 歌单 / 播放（✅ 完成并验证）
- P1：B站下载（480p 零依赖）/ 弹幕渲染 / 评论展示（✅ 完成并真实验证）
- P2：AI 增强 —— 响度统一 ✅ 已验证；**原版/增强版一键切换 ✅（B站切画质式体验）**；画质/降噪/超分代码完成，待 AI 依赖
- P3：发弹幕 / 稍后再看 / 仿 B 站首页（✅ 播放模式/最近播放/首页已实现）

## 后端自检

```bash
cd backend
py -3.10 selfcheck.py    # 8 项回归：数据库/增强器/B站接口/服务层
```
