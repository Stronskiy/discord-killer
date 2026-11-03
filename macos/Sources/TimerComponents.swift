import SwiftUI

struct TimerCountdownRing: View {
    @ObservedObject var controller: TimerController
    var compact = false

    private var accent: Color {
        controller.isClosingDiscord ? .orange : .indigo
    }

    var body: some View {
        ZStack {
            Circle()
                .stroke(accent.opacity(0.16), lineWidth: compact ? 7 : 10)

            Circle()
                .trim(from: 0, to: controller.isRunning ? controller.progress : 1)
                .stroke(
                    AngularGradient(colors: [accent.opacity(0.35), accent.opacity(0.72), accent], center: .center),
                    style: StrokeStyle(lineWidth: compact ? 7 : 10, lineCap: .round)
                )
                .rotationEffect(.degrees(-90))

            VStack(spacing: compact ? 3 : 7) {
                if controller.isClosingDiscord {
                    ProgressView()
                        .controlSize(compact ? .small : .regular)
                        .tint(.orange)
                    Text("ЗАКРЫТИЕ")
                        .font(.system(size: compact ? 9 : 10, weight: .semibold, design: .rounded))
                        .tracking(1.1)
                        .foregroundStyle(.secondary)
                } else {
                    if controller.isRunning {
                        Text(controller.remainingText)
                            .font(.system(size: compact ? 21 : 42, weight: .medium, design: .rounded))
                            .monospacedDigit()
                    } else {
                        DurationInput(controller: controller, large: !compact)
                            .frame(width: compact ? 78 : 135)
                    }
                    Text(controller.isRunning ? "до закрытия" : "минут · изменить")
                        .font(.system(size: compact ? 9 : 11, weight: .medium))
                        .foregroundStyle(.secondary)
                }
            }
        }
        .padding(compact ? 10 : 16)
        .timerGlassSurface()
        .frame(width: compact ? 130 : 240, height: compact ? 130 : 240)
    }
}

struct DurationInput: View {
    @ObservedObject var controller: TimerController
    var large = false

    var body: some View {
        TextField("Минуты", text: Binding(
            get: { controller.durationDraft },
            set: { controller.setDurationDraft($0) }
        ))
            .textFieldStyle(.plain)
            .multilineTextAlignment(.center)
            .font(.system(size: large ? 42 : 16, weight: .medium, design: .rounded))
            .monospacedDigit()
            .accessibilityLabel("Время таймера в минутах, от 1 до 720")
    }
}

struct DurationControls: View {
    @ObservedObject var controller: TimerController
    var body: some View {
        HStack(spacing: 10) {
            Text("Свое время")
                .foregroundStyle(.secondary)
            Spacer()
            DurationInput(controller: controller)
                .frame(width: 52)
                .padding(.vertical, 5)
                .background(.quaternary, in: RoundedRectangle(cornerRadius: 8))
            Text("мин")
                .foregroundStyle(.secondary)
            Stepper("Продолжительность", value: $controller.selectedMinutes, in: 1...720)
                .labelsHidden()
        }
        .font(.callout)
    }
}

extension View {
    @ViewBuilder func timerGlassSurface() -> some View {
        if #available(macOS 26.0, *) {
            self.glassEffect(.regular.tint(.indigo.opacity(0.14)), in: .circle)
                .overlay(Circle().stroke(.white.opacity(0.18), lineWidth: 1).allowsHitTesting(false))
        } else {
            self.background(.regularMaterial, in: Circle())
                .overlay(Circle().stroke(.white.opacity(0.18), lineWidth: 1).allowsHitTesting(false))
        }
    }
}

struct TimerPresetPicker: View {
    @ObservedObject var controller: TimerController
    var compact = false
    private let presets = [15, 30, 45, 60]

    var body: some View {
        Group {
            if #available(macOS 26.0, *) {
                GlassEffectContainer(spacing: compact ? 6 : 9) {
                    presetButtons
                }
            } else {
                presetButtons
            }
        }
    }

    private var presetButtons: some View {
        HStack(spacing: compact ? 6 : 9) {
            ForEach(presets, id: \.self) { minutes in
                Button {
                    controller.selectedMinutes = minutes
                } label: {
                    Text(compact ? "\(minutes)" : "\(minutes) мин")
                        .font(.system(size: compact ? 12 : 13, weight: .medium, design: .rounded))
                        .frame(maxWidth: .infinity)
                        .frame(height: compact ? 31 : 36)
                        .contentShape(Capsule())
                }
                .adaptiveGlassButton(prominent: controller.selectedMinutes == minutes)
                .tint(.indigo)
                .disabled(controller.isRunning || controller.isClosingDiscord)
                .help("\(minutes) минут")
                .accessibilityLabel("\(minutes) минут")
                .accessibilityAddTraits(controller.selectedMinutes == minutes ? .isSelected : [])
            }
        }
    }
}
