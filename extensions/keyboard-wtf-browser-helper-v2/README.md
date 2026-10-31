# QLOO Jarvis Chrome Helper

This Manifest V3 extension connects the QLOO desktop app to explicitly requested Chrome tasks. It uses a QLOO-specific native host and extension identity; it does not replace or register over the separate keyboard.wtf Helper.

## Install for development

1. Run QLOO and open Settings from its tray menu.
2. Choose **Prepare QLOO Chrome Helper**.
3. Open `chrome://extensions` and enable Developer mode.
4. Choose **Load unpacked** and select the prepared Helper folder.
5. Return to QLOO Settings and check the Helper connection status.

The extension remains idle when Jarvis has no active browser task. It stores no API keys and uses the existing tab/window identity and safety checks from the desktop app.

This is an unpacked development helper. Public Chrome Web Store publishing and a public privacy page have not been completed.
