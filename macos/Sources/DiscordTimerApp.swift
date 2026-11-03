import AppKit
import SwiftUI

@main
struct DiscordTimerApp: App {
    @StateObject private var timerController = TimerController()

    var body: some Scene {
        WindowGroup("Discord Timer", id: "main") {
            TimerWindow(controller: timerController)
        }
        .defaultSize(width: 420, height: 510)
        .windowResizability(.contentSize)

        MenuBarExtra {
            TimerMenu(controller: timerController)
        } label: {
            HStack(spacing: 4) {
                Image(nsImage: MenuBarMark.image)
                if timerController.isRunning || timerController.isClosingDiscord {
                    Text(menuBarTitle).monospacedDigit()
                }
            }
            .accessibilityLabel("Discord Timer, \(menuBarTitle)")
        }
        .menuBarExtraStyle(.window)
    }

    private var menuBarTitle: String {
        if timerController.isRunning { return timerController.remainingText }
        if timerController.isClosingDiscord { return "Закрытие…" }
        return "Discord Timer"
    }
}

/// A template image lets macOS select the appropriate color for the menu bar.
enum MenuBarMark {
    static let image: NSImage = {
        let image = NSImage(size: NSSize(width: 19, height: 19), flipped: false) { _ in
            NSColor.black.setStroke()
            let dial = NSBezierPath(ovalIn: NSRect(x: 3, y: 3, width: 13, height: 13))
            dial.lineWidth = 1.6
            dial.stroke()
            let hands = NSBezierPath()
            hands.lineWidth = 1.5
            hands.lineCapStyle = .round
            hands.move(to: NSPoint(x: 9.5, y: 13))
            hands.line(to: NSPoint(x: 9.5, y: 9.5))
            hands.line(to: NSPoint(x: 12, y: 8))
            hands.stroke()
            NSColor.black.setFill()
            NSBezierPath(ovalIn: NSRect(x: 1, y: 15.5, width: 2, height: 2)).fill()
            NSBezierPath(ovalIn: NSRect(x: 16, y: 1, width: 2, height: 2)).fill()
            return true
        }
        image.isTemplate = true
        return image
    }()
}

extension View {
    @ViewBuilder
    func adaptiveGlassButton(prominent: Bool = false) -> some View {
        if #available(macOS 26.0, *) {
            if prominent {
                buttonStyle(.glassProminent)
            } else {
                buttonStyle(.glass)
            }
        } else if prominent {
            buttonStyle(.borderedProminent)
        } else {
            buttonStyle(.bordered)
        }
    }
}
