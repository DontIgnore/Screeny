import AppKit

/// What windows can ask the app to do. The AppDelegate implements it; windows get it at creation.
protocol AppServices: AnyObject {
    var settings: AppSettings { get }
    /// Persists `settings` and applies changes (hotkeys, login item, language, features). Returns false and shows
    /// the error itself if saving failed.
    @discardableResult func saveSettings() -> Bool
    var history: HistoryStore { get }
    /// History changed (new entry, deletion): open history windows refresh.
    var historyChanged: Notification.Name { get }

    func startCapture()
    func showHistory()
    func showSettings()
    func showSetupWizard()
    func showFeedback(bug: Bool)
    func checkForUpdates(interactive: Bool)
    /// Opens a history entry in the editor window (re-editing with its markup).
    func openEditor(_ item: HistoryItem)
    /// Pins a picture on top of all windows.
    func pin(_ image: CGImage, scale: CGFloat)
    /// Copies a picture: PNG + TIFF on the general pasteboard. Returns false if it failed.
    @discardableResult func copyImage(_ image: CGImage, scale: CGFloat) -> Bool
    /// Small non-blocking notice (bottom of the screen).
    func toast(_ text: String)
    /// Current capture shortcuts for texts ("⌘⇧2").
    var hotkeyText: String? { get }
}
