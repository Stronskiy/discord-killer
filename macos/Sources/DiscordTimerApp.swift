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
            // An original controller outline with a slash and compact timer badge.
            // Template drawing lets macOS choose the right contrast in both themes.
            NSColor.black.setStroke()
            let pad = NSBezierPath()
            pad.lineWidth = 1.25
            pad.lineJoinStyle = .round
            pad.move(to: NSPoint(x: 5.0, y: 11.5))
            pad.curve(to: NSPoint(x: 2.7, y: 8.9), controlPoint1: NSPoint(x: 3.7, y: 11.4), controlPoint2: NSPoint(x: 3.0, y: 10.4))
            pad.line(to: NSPoint(x: 1.8, y: 5.8))
            pad.curve(to: NSPoint(x: 4.4, y: 4.4), controlPoint1: NSPoint(x: 1.3, y: 4.0), controlPoint2: NSPoint(x: 3.0, y: 3.2))
            pad.line(to: NSPoint(x: 7.0, y: 6.3))
            pad.line(to: NSPoint(x: 11.5, y: 6.3))
            pad.line(to: NSPoint(x: 14.1, y: 4.4))
            pad.curve(to: NSPoint(x: 16.7, y: 5.8), controlPoint1: NSPoint(x: 15.5, y: 3.2), controlPoint2: NSPoint(x: 17.2, y: 4.0))
            pad.line(to: NSPoint(x: 15.8, y: 8.9))
            pad.curve(to: NSPoint(x: 13.5, y: 11.5), controlPoint1: NSPoint(x: 15.5, y: 10.4), controlPoint2: NSPoint(x: 14.8, y: 11.4))
            pad.close()
            pad.stroke()
            let cross = NSBezierPath()
            cross.lineWidth = 1.15
            cross.lineCapStyle = .round
            cross.move(to: NSPoint(x: 4.7, y: 8.7)); cross.line(to: NSPoint(x: 7.2, y: 8.7))
            cross.move(to: NSPoint(x: 5.95, y: 7.45)); cross.line(to: NSPoint(x: 5.95, y: 9.95))
            cross.stroke()
            NSColor.black.setFill()
            NSBezierPath(ovalIn: NSRect(x: 12.2, y: 8.2, width: 1.4, height: 1.4)).fill()
            let slash = NSBezierPath()
            slash.lineWidth = 1.65
            slash.lineCapStyle = .round
            slash.move(to: NSPoint(x: 3.2, y: 15.2)); slash.line(to: NSPoint(x: 15.2, y: 3.2))
            slash.stroke()
            let badge = NSBezierPath(ovalIn: NSRect(x: 12.2, y: 12.2, width: 4.6, height: 4.6))
            badge.lineWidth = 1.1
            badge.stroke()
            let hand = NSBezierPath()
            hand.lineWidth = 0.9
            hand.lineCapStyle = .round
            hand.move(to: NSPoint(x: 14.5, y: 15.9)); hand.line(to: NSPoint(x: 14.5, y: 14.4)); hand.line(to: NSPoint(x: 15.4, y: 14.9))
            hand.stroke()
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
