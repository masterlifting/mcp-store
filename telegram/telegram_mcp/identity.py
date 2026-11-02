"""Resolve the Telethon device identity used when connecting to Telegram.

The values are re-sent on every connection, so unset defaults would overwrite
the device name chosen at login on each reconnect.
"""

import os

# Maps the Telethon constructor keyword to the environment variable that sets it.
_DEVICE_ENV = {
    "device_model": "TELEGRAM_DEVICE_MODEL",
    "system_version": "TELEGRAM_SYSTEM_VERSION",
    "app_version": "TELEGRAM_APP_VERSION",
}


def client_identity_kwargs() -> dict:
    """Return TelegramClient device kwargs derived from the environment.

    Only variables that are set to a non-empty value are included, so unset
    ones keep Telethon's own defaults.
    """
    kwargs = {}
    for kwarg, env_var in _DEVICE_ENV.items():
        value = os.environ.get(env_var)
        if value:
            kwargs[kwarg] = value
    return kwargs
