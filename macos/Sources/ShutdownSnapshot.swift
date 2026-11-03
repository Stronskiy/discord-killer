/// Retains the exact application objects present when shutdown started.
/// A later launch is never added to the force-close set.
struct ShutdownSnapshot<T> {
    private let targets: [T]

    init(_ targets: [T]) { self.targets = targets }

    func remaining(whereTerminated isTerminated: (T) -> Bool) -> [T] {
        targets.filter { !isTerminated($0) }
    }
}
