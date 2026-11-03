import AppKit
import Combine
import Foundation

@MainActor
final class TimerController: ObservableObject {
    @Published var selectedMinutes = 30 {
        didSet { durationDraft = String(selectedMinutes) }
    }
    @Published private(set) var durationDraft = "30"
    @Published private(set) var deadline: Date?
    @Published private(set) var currentTime = Date()
    @Published private(set) var isClosingDiscord = false
    @Published private(set) var statusMessage = "Выберите время и запустите таймер."

    private var countdownTimer: Timer?
    private var forceCloseTimer: Timer?
    private var shutdownSnapshot: ShutdownSnapshot<NSRunningApplication>?

    private static let discordBundleIdentifier = "com.hnc.Discord"

    var isRunning: Bool {
        deadline != nil
    }

    var validDuration: Int? {
        guard !durationDraft.isEmpty,
              durationDraft.utf8.allSatisfy({ (48...57).contains($0) }),
              let value = Int(durationDraft), (1...720).contains(value) else { return nil }
        return value
    }

    func setDurationDraft(_ value: String) {
        guard !isRunning && !isClosingDiscord else { return }
        durationDraft = value
        if let minutes = validDuration, selectedMinutes != minutes {
            selectedMinutes = minutes
            durationDraft = value
        }
    }

    var remainingText: String {
        guard deadline != nil else { return "00:00" }
        let seconds = remainingSeconds
        return String(format: "%02d:%02d", seconds / 60, seconds % 60)
    }

    var remainingSeconds: Int {
        guard let deadline else { return 0 }
        return max(0, Int(ceil(deadline.timeIntervalSince(currentTime))))
    }

    var progress: Double {
        guard isRunning else { return 0 }
        let duration = Double(max(selectedMinutes, 1) * 60)
        return min(max(Double(remainingSeconds) / duration, 0), 1)
    }

    var selectedDurationText: String {
        String(format: "%02d:00", selectedMinutes)
    }

    func start(minutes: Int? = nil) {
        guard !isRunning && !isClosingDiscord else { return }

        if let minutes {
            selectedMinutes = min(max(minutes, 1), 720)
        }
        guard let validMinutes = validDuration else { return }
        selectedMinutes = validMinutes

        let now = Date()
        currentTime = now
        statusMessage = "Таймер запущен. Discord закроется через \(selectedMinutes) мин."
        deadline = now.addingTimeInterval(TimeInterval(selectedMinutes * 60))
        scheduleCountdown()
    }

    func cancel() {
        countdownTimer?.invalidate()
        countdownTimer = nil
        forceCloseTimer?.invalidate()
        forceCloseTimer = nil
        shutdownSnapshot = nil
        isClosingDiscord = false
        deadline = nil
        statusMessage = "Таймер отменён."
    }

    private func scheduleCountdown() {
        countdownTimer?.invalidate()
        let timer = Timer(timeInterval: 1, repeats: true) { [weak self] _ in
            Task { @MainActor in
                self?.checkDeadline()
            }
        }
        countdownTimer = timer
        RunLoop.main.add(timer, forMode: .common)
    }

    private func checkDeadline() {
        currentTime = Date()
        guard let deadline else { return }
        guard deadline <= Date() else { return }

        countdownTimer?.invalidate()
        countdownTimer = nil
        self.deadline = nil
        closeDiscord()
    }

    private func closeDiscord() {
        let applications = runningDiscordApplications()
        guard !applications.isEmpty else {
            isClosingDiscord = false
            statusMessage = "Discord уже закрыт."
            return
        }

        isClosingDiscord = true
        shutdownSnapshot = ShutdownSnapshot(applications)
        statusMessage = "Отправляю Discord запрос на выход…"
        applications.forEach { _ = $0.terminate() }

        forceCloseTimer?.invalidate()
        let timer = Timer(timeInterval: 10, repeats: false) { [weak self] _ in
            Task { @MainActor in
                self?.forceCloseDiscordIfNeeded()
            }
        }
        forceCloseTimer = timer
        RunLoop.main.add(timer, forMode: .common)
    }

    private func forceCloseDiscordIfNeeded() {
        forceCloseTimer = nil
        let remainingApplications = shutdownSnapshot?.remaining(whereTerminated: { $0.isTerminated }) ?? []
        shutdownSnapshot = nil
        guard !remainingApplications.isEmpty else {
            isClosingDiscord = false
            statusMessage = "Discord закрыт штатно."
            return
        }

        let allRequestsAccepted = remainingApplications
            .map { $0.forceTerminate() }
            .allSatisfy { $0 }
        isClosingDiscord = false
        statusMessage = allRequestsAccepted
            ? "Команда принудительного закрытия отправлена. Черновик мог не сохраниться."
            : "Не удалось отправить команду закрытия Discord. Проверьте приложение."
    }

    private func runningDiscordApplications() -> [NSRunningApplication] {
        NSRunningApplication.runningApplications(
            withBundleIdentifier: Self.discordBundleIdentifier
        ).filter { !$0.isTerminated }
    }
}
