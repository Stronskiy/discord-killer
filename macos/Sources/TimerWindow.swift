import SwiftUI

struct TimerWindow: View {
    @ObservedObject var controller: TimerController

    var body: some View {
        VStack(spacing: 0) {
            header
                .padding(.top, 24)

            TimerCountdownRing(controller: controller)
                .padding(.top, 20)

            if controller.isRunning || controller.isClosingDiscord {
                runningActions
                    .padding(.top, 22)
            } else {
                durationControls
                    .padding(.top, 20)
            }

            Text(controller.statusMessage)
                .font(.callout)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: .infinity)
                .padding(.top, 14)

            Spacer(minLength: 12)

            Label("Discord закроется принудительно, если не выйдет за 10 секунд.", systemImage: "info.circle")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.bottom, 20)
        }
        .padding(.horizontal, 30)
        .frame(width: 440, height: 560)
        .background {
            ZStack {
                Rectangle().fill(.regularMaterial)
                LinearGradient(
                    colors: [Color.indigo.opacity(0.13), Color.purple.opacity(0.045), Color.clear],
                    startPoint: .topLeading,
                    endPoint: .bottomTrailing
                )
            }
            .ignoresSafeArea()
        }
    }

    private var header: some View {
        HStack(spacing: 9) {
            Image(systemName: "timer")
                .font(.system(size: 19, weight: .semibold))
                .foregroundStyle(.indigo)
                .accessibilityHidden(true)
            Text("Discord Timer")
                .font(.system(size: 19, weight: .semibold, design: .rounded))
        }
        .frame(maxWidth: .infinity)
        .accessibilityElement(children: .combine)
    }

    private var durationControls: some View {
        VStack(spacing: 12) {
            TimerPresetPicker(controller: controller)
            DurationControls(controller: controller)

            if controller.validDuration == nil {
                Text("Введите целое число от 1 до 720 минут.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }

            Button {
                controller.start()
            } label: {
                Label("Запустить таймер", systemImage: "play.fill")
                    .font(.system(size: 14, weight: .semibold))
                    .frame(maxWidth: .infinity)
                    .frame(height: 40)
            }
            .adaptiveGlassButton(prominent: true)
            .tint(.indigo)
            .keyboardShortcut(.defaultAction)
            .disabled(controller.validDuration == nil)
        }
    }

    private var runningActions: some View {
        Button {
            controller.cancel()
        } label: {
            Label(controller.isClosingDiscord ? "Завершаю Discord…" : "Отменить таймер", systemImage: "xmark.circle")
                .frame(maxWidth: .infinity)
                .frame(height: 40)
        }
        .adaptiveGlassButton()
        .disabled(controller.isClosingDiscord)
    }
}
