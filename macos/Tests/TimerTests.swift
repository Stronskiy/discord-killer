import AppKit
import Foundation

@main
struct TimerTests {
    @MainActor
    static func main() {
        var count = 0
        func check(_ condition: @autoclosure () -> Bool, _ message: String) {
            guard condition() else {
                fputs("FAIL: \(message)\n", stderr)
                exit(1)
            }
            count += 1
        }

        let controller = TimerController()
        check(controller.durationDraft == "30", "default duration")
        for value in ["", "0", "721", "-1", "1 0", "abc", "1.5", "١"] {
            controller.setDurationDraft(value)
            check(controller.validDuration == nil, "reject invalid duration \(value)")
            controller.start()
            check(!controller.isRunning, "invalid duration must not start: \(value)")
        }
        for (value, expected) in [("1", 1), ("720", 720), ("0001", 1)] {
            controller.setDurationDraft(value)
            check(controller.validDuration == expected, "accept duration \(value)")
            check(controller.selectedMinutes == expected, "valid draft updates selected minutes")
        }
        controller.selectedMinutes = 45
        check(controller.durationDraft == "45", "presets update the shared input")
        controller.start()
        check(controller.isRunning, "valid duration starts")
        controller.setDurationDraft("2")
        check(controller.durationDraft == "45", "active input is not editable")
        controller.cancel()
        check(!controller.isRunning, "cancel clears running timer")

        struct FakeApplication { let id: Int; let terminated: Bool }
        let original = [FakeApplication(id: 1, terminated: true), FakeApplication(id: 2, terminated: false)]
        let snapshot = ShutdownSnapshot(original)
        let newlyLaunched = FakeApplication(id: 3, terminated: false)
        let remaining = snapshot.remaining(whereTerminated: { $0.terminated })
        check(remaining.map(\.id) == [2], "only original live target remains")
        check(!remaining.contains(where: { $0.id == newlyLaunched.id }), "newly launched app is excluded")
        print("Passed \(count) timer and snapshot assertions.")
    }
}
