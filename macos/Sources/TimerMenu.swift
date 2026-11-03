import AppKit
import SwiftUI

struct TimerMenu: View {
    @ObservedObject var controller: TimerController
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        VStack(spacing: 14) {
            HStack(spacing: 12) {
                TimerCountdownRing(controller: controller, compact: true)
                VStack(alignment: .leading, spacing: 5) {
                    Text("Discord Timer")
                        .font(.headline)
                    Text(stateDescription)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 0)
            }

            if controller.isRunning || controller.isClosingDiscord {
                Button {
                    controller.cancel()
                } label: {
                    Label(controller.isClosingDiscord ? "Discord завершает работу" : "Отменить таймер", systemImage: "xmark.circle")
                        .frame(maxWidth: .infinity)
                }
                .adaptiveGlassButton()
                .disabled(controller.isClosingDiscord)
            } else {
                TimerPresetPicker(controller: controller, compact: true)

                DurationControls(controller: controller)

                if controller.validDuration == nil {
                    Text("Введите целое число от 1 до 720 минут.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }

                Button {
                    controller.start()
                } label: {
                    Label("Запустить", systemImage: "play.fill")
                        .frame(maxWidth: .infinity)
                }
                .adaptiveGlassButton(prominent: true)
                .tint(.indigo)
                .keyboardShortcut(.defaultAction)
                .disabled(controller.validDuration == nil)
            }

            Divider()

            HStack {
                Button {
                    openWindow(id: "main")
                } label: {
                    Label("Открыть окно", systemImage: "macwindow")
                }
                .buttonStyle(.plain)

                Spacer()

                Button("Выход") {
                    NSApplication.shared.terminate(nil)
                }
                .buttonStyle(.plain)
                .foregroundStyle(.secondary)
                .keyboardShortcut("q")
            }
            .font(.caption)
        }
        .padding(16)
        .frame(width: 320)
    }

    private var stateDescription: String {
        if controller.isClosingDiscord { return "Ожидаю завершения Discord…" }
        if controller.isRunning { return "Осталось \(controller.remainingText)" }
        return controller.statusMessage
    }
}
