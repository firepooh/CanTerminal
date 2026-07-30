from .client import DEFAULT_PORT, CanTerminalClient, CanTerminalError

try:
    from .bus import CanTerminalBus  # requires python-can
except ImportError:  # python-can not installed; raw client still usable
    CanTerminalBus = None  # type: ignore[assignment,misc]

__all__ = ["CanTerminalClient", "CanTerminalError", "CanTerminalBus", "DEFAULT_PORT"]
__version__ = "1.0.0"
