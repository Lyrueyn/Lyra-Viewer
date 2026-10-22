"""本地歌单器 · Python 后端启动入口

由 C++ 主程序（BackendManager）自动拉起；也可手动运行：
    python run_backend.py --port 8765

默认使用标准库版服务（零第三方依赖）；若已安装 fastapi/uvicorn 仍使用标准库版，
保持行为一致（路由逻辑在 app/services.py 共享）。
"""
from __future__ import annotations

import argparse

from app.stdlib_server import main

if __name__ == "__main__":
    main()
