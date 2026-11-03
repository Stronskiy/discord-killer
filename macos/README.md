# macOS app

The macOS prototype provides a shared timer in a Dock window and a menu bar panel. It is separate from the Windows implementation and uses original timer artwork.

## Build and verify

Requires Apple Silicon, Xcode with the macOS 26 SDK, and macOS 13 or later to run. From the repository root:

```sh
bash macos/Scripts/test.sh
bash macos/Scripts/build-app.sh
```

The build creates `macos/dist/Discord Timer.app` and `macos/dist/Discord Timer.zip`, signed locally with an ad-hoc signature. The archive is not notarized. Build and timer tests do not demonstrate a real Discord shutdown, system sleep behavior or the graphical menu; those require separate interactive validation.

Choose 15, 30, 45 or 60 minutes or enter an integer from 1 to 720. The app must stay running. At expiry it asks Discord to quit and, if an originally detected instance remains after ten seconds, sends a force-quit request. Unsaved drafts may be lost. The force-quit request is not a guarantee that the process has exited.
