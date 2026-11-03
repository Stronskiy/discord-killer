import SwiftUI

struct TimerWindow: View {
    @ObservedObject var controller: TimerController

    var body: some View {
        VStack(spacing: 0) {
            header
                .padding(.top, 24)

            TimerCountdownRing(controller: controller)
                .padding(.top, 12)

            if controller.isRunning || controller.isClosingDiscord {
                runningActions
                    .padding(.top, 22)
            } else {
                durationControls
                    .padding(.top, 19)
            }

            Text(controller.statusMessage)
                .font(.callout)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: .infinity)
                .padding(.top, 15)

            Spacer(minLength: 12)

            Label("Discord закроется принудительно, если не выйдет за 10 секунд.", systemImage: "info.circle")
                .font(.caption)
                .foregroundStyle(.tertiary)
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.bottom, 18)
        }
        .padding(.horizontal, 30)
        .frame(width: 420, height: 510)
        .background {
            LinearGradient(
                colors: [Color.indigo.opacity(0.045), Color.clear, Color.primary.opacity(0.012)],
                startPoint: .topLeading,
                endPoint: .bottomTrailing
            )
            .ignoresSafeArea()
        }
    }

    private var header: some View {
        HStack(spacing: 12) {
            Image(systemName: "timer")
                .font(.system(size: 20, weight: .semibold))
                .foregroundStyle(.indigo)
                .frame(width: 40, height: 40)
                .background(.indigo.opacity(0.1), in: RoundedRectangle(cornerRadius: 13, style: .continuous))
                .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: 2) {
                Text("Discord Timer")
                    .font(.headline)
                Text("Время для цифровой паузы")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            }
            Spacer()
            Circle()
                .fill(controller.isRunning ? .indigo : .green)
                .frame(width: 7, height: 7)
                .accessibilityHidden(true)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
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
