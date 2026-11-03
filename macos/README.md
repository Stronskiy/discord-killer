# macOS app

The macOS prototype provides a shared timer in a Dock window and a menu bar panel. It is separate from the Windows implementation and uses original timer artwork.

## Build and verify

Requires Apple Silicon, Xcode with the macOS 26 SDK, and macOS 13 or later to run. From the repository root:

```sh
bash macos/Scripts/test.sh
bash macos/Scripts/build-app.sh
python3 -m pip install -r macos/Packaging/requirements.txt
bash macos/Scripts/build-dmg.sh
bash macos/Scripts/test-dmg.sh
```

The build creates `macos/dist/Discord Timer.app`, the fallback `Discord Timer.zip`, and `Discord-Timer-macOS-arm64.dmg`. Open the DMG and drag Discord Timer to Applications, then eject the image. The app uses an ad-hoc signature, and neither the app nor image is notarized; Gatekeeper may block it. An existing app with the same name and bundle identifier may be replaced when copying. Build and timer tests do not demonstrate a real Discord shutdown, system sleep behavior or the graphical menu; those require separate interactive validation.

Choose 15, 30, 45 or 60 minutes or enter an integer from 1 to 720. The app must stay running. At expiry it asks Discord to quit and, if an originally detected instance remains after ten seconds, sends a force-quit request. Unsaved drafts may be lost. The force-quit request is not a guarantee that the process has exited.
