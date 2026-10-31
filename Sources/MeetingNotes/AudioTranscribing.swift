import AVFoundation

protocol AudioTranscribing: AnyObject, Sendable {
    func append(_ buffer: AVAudioPCMBuffer, at offset: TimeInterval)
    func finish() async
    func cancel() async
}
