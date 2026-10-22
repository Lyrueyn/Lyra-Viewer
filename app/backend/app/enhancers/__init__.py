"""增强器插件目录：导入即把各增强器注册进 ai_pipeline.ENHANCERS"""
from . import audio_sr  # noqa: F401
from . import denoise  # noqa: F401
from . import fft_hires  # noqa: F401
from . import loudness  # noqa: F401
from . import realesrgan  # noqa: F401
from . import rife  # noqa: F401
from . import soxr_hires  # noqa: F401
