import AppKit

extension AppDelegate {
    func checkForUpdates(interactive: Bool) { UpdateChecker.run(services: self, interactive: interactive) }
    func startUpdateChecks() { UpdateChecker.startBackgroundChecks(services: self) }
}
