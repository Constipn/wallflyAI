using System.Collections.ObjectModel;
using System.Diagnostics;
using Wallfly.Core.Persistence;
using Wallfly.Core.Text;

namespace Wallfly.Core.Services;

/// <summary>
/// After the recording: speaker matching and editing hooks, automatic audio deletion (spec 8), the transcript mirror
/// (spec 8) and moving the library (spec 8).
/// </summary>
public sealed partial class MeetingLibraryService
{
    private static readonly IReadOnlyDictionary<Guid, string> NoMirrorErrors = new ReadOnlyDictionary<Guid, string>(new Dictionary<Guid, string>());

    private readonly TranscriptMirrorWriter mirror = new();
    private readonly Dictionary<Guid, string> mirrorErrors = [];
    private IReadOnlyDictionary<Guid, string> mirrorErrorsView = NoMirrorErrors;
    /// <summary>Meetings stopped while quitting: their post-recording steps run if the quit is called off.</summary>
    private readonly List<(Guid MeetingID, bool Complete)> deferredAfterStop = [];

    // ---- speakers (spec 5.3 – 5.5) -------------------------------------------------------------------------------

    /// <summary>"Rename speaker…", "Assign this segment" and "Name this passage…"; null when the app runs without the hooks.</summary>
    public ISpeakerEditor? SpeakerEditor => hooks.SpeakerEditor;

    /// <summary>The queued work after a stop (speaker matching).</summary>
    public IReadOnlyList<IPostRecordingStep> PostRecordingSteps => hooks.PostRecordingSteps;

    /// <summary>"Matching speakers… 40%" while a step runs; empty when idle.</summary>
    public string SpeakerMatchingProgress => hooks.PostRecordingSteps.Select(step => step.Progress).FirstOrDefault(text => text.Length > 0) ?? "";

    /// <summary>A speaker matching run is queued or running for the meeting.</summary>
    public bool IsMatchingSpeakers(Guid meetingID) => hooks.PostRecordingSteps.Any(step => step.IsPending(meetingID));

    /// <summary>Whether "Re-detect speakers" can start now (setting on, word times and remote audio present, not recording).</summary>
    public bool CanRedetectSpeakers(Guid meetingID) =>
        !closing && !IsMovingLibrary && !IsRecordingMeeting(meetingID) && hooks.PostRecordingSteps.Any(step => step.CanRun(meetingID));

    /// <summary>"Re-detect speakers": queues the post-recording steps for the meeting. False when nothing was queued.</summary>
    public bool RedetectSpeakers(Guid meetingID)
    {
        if (closing || IsMovingLibrary || IsRecordingMeeting(meetingID)) return false;
        var queued = false;
        foreach (var step in hooks.PostRecordingSteps) queued |= step.Enqueue(meetingID);
        return queued;
    }

    /// <summary>
    /// "Re-detect speakers on this device": as <see cref="RedetectSpeakers"/>, but the speaker separation runs on this computer
    /// even when the setting chooses the cloud (the user's explicit choice). False when nothing was queued.
    /// </summary>
    public bool RedetectSpeakersOnDevice(Guid meetingID)
    {
        if (closing || IsMovingLibrary || IsRecordingMeeting(meetingID)) return false;
        var queued = false;
        foreach (var step in hooks.PostRecordingSteps) queued |= step.EnqueueOnDevice(meetingID);
        return queued;
    }

    /// <summary>Completes once no step has queued or running work (tests, quit diagnostics).</summary>
    public Task WhenSpeakerMatchingIdle() => Task.WhenAll(hooks.PostRecordingSteps.Select(step => step.WhenIdle()));

    private bool IsRecordingMeeting(Guid meetingID) => (IsRecording || IsStarting || IsStopping) && ActiveID == meetingID;

    /// <summary>
    /// After a stop (macOS <c>stopRecording</c>' last lines): a complete recording without speaker matching is evaluated
    /// for audio deletion at once; otherwise the post-recording steps are queued, which evaluate it after a successful run.
    /// </summary>
    private void AfterStop(Guid meetingID, bool complete)
    {
        if (AudioDeletionPolicy.DeleteAtStop(complete, Settings)) { MarkAudioReadyForDeletion(meetingID); return; }
        foreach (var step in hooks.PostRecordingSteps)
        {
            try { step.Enqueue(meetingID); }
            catch (Exception failure) { Trace.TraceError("Wallfly: queueing a post-recording step failed: " + failure.Message); }
        }
    }

    /// <summary>The meeting or its processed archives are gone: the steps drop what they derived from them (cached uploads).</summary>
    private void AudioRemoved(Guid meetingID)
    {
        foreach (var step in hooks.PostRecordingSteps)
        {
            try { step.MeetingAudioRemoved(meetingID); }
            catch (Exception failure) { Trace.TraceError("Wallfly: telling a post-recording step about removed audio failed: " + failure.GetType().Name); }
        }
    }

    /// <summary>Saves an edited copy first, then publishes its speaker state into the live meeting (spec 5.5).</summary>
    private bool SaveEdited(Meeting live, Meeting edited, string failurePrefix)
    {
        if (IsMovingLibrary) return false;
        try { Writer.WriteNow(edited); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            Error = failurePrefix + failure.Message;
            return false;
        }
        live.AdoptSpeakerState(edited);
        Updated?.Invoke();
        return true;
    }

    // ---- automatic audio deletion (spec 8) ----------------------------------------------------------------------

    /// <summary>
    /// macOS <c>markAudioReadyForDeletion</c>: a meeting whose enrolled recordings are fully processed (complete transcript,
    /// no recovery needed; with speaker matching on, called only after a successful run) is marked and cleaned up.
    /// </summary>
    private void MarkAudioReadyForDeletion(Guid meetingID)
    {
        if (Find(meetingID) is not { } meeting || !AudioDeletionPolicy.CanMarkReady(meeting)) return;
        meeting.AudioDeletionReady = true;
        CleanupProcessedAudio(meetingID);
    }

    /// <summary>
    /// "Try again" under Storage, and the retry at every start: deletes the enrolled recordings of a meeting marked ready
    /// (<see cref="Meeting.AudioDeletionReady"/>). The decision is saved before any file is unlinked; only <c>.wav</c>
    /// files directly inside <c>Audio\&lt;MeetingID&gt;\</c> are removed, never links. A failure keeps the audio, records
    /// <see cref="Meeting.AudioDeletionError"/> and shows the banner.
    /// </summary>
    public void RetryAudioCleanup(Guid meetingID) => CleanupProcessedAudio(meetingID);

    private void CleanupProcessedAudio(Guid meetingID)
    {
        if (IsMovingLibrary || IsRecordingMeeting(meetingID)) return;
        if (Find(meetingID) is not { AudioDeletionReady: true } meeting) return;
        try
        {
            Writer.WriteNow(meeting);
            foreach (var path in (meeting.AudioDeletionPending ?? []).ToList())
            {
                Library.RemoveAudioFile(meetingID, path);
                meeting.AudioFiles.RemoveAll(file => Library.SameAudioFile(file, path));
                meeting.AudioDeletionPending?.RemoveAll(file => Library.SameAudioFile(file, path));
            }
            meeting.AudioDeletionError = null;
            Writer.WriteNow(meeting);
            AudioRemoved(meetingID);
            Updated?.Invoke();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            meeting.AudioDeletionError = failure.Message;
            Persist(meeting);
            Error = CoreMessages.AudioDeletionFailed(failure.Message);
        }
    }

    // ---- transcript mirror (spec 8) -----------------------------------------------------------------------------

    /// <summary>The last mirror failure per meeting (the meeting itself is saved); empty when every mirror succeeded.</summary>
    public IReadOnlyDictionary<Guid, string> TranscriptMirrorErrors => mirrorErrorsView;

    /// <summary>Writes the meeting (and so its mirror) again, e.g. after the mirror folder came back.</summary>
    public void RetryTranscriptMirror(Guid meetingID)
    {
        if (!Settings.TranscriptMirrorEnabled || Find(meetingID) is not { TranscriptMirror: not null } meeting) return;
        MarkDirty(meeting);
        Flush();
    }

    private void SetMirrorError(Guid meetingID, string? message)
    {
        var changed = message is null
            ? mirrorErrors.Remove(meetingID)
            : Find(meetingID) is not null && (!mirrorErrors.TryGetValue(meetingID, out var previous) || previous != message);
        if (message is not null && changed) mirrorErrors[meetingID] = message;
        if (!changed) return;
        mirrorErrorsView = mirrorErrors.Count == 0 ? NoMirrorErrors : new ReadOnlyDictionary<Guid, string>(new Dictionary<Guid, string>(mirrorErrors));
        Changed(nameof(TranscriptMirrorErrors));
    }

    private void ClearMirrorErrors()
    {
        if (mirrorErrors.Count == 0) return;
        mirrorErrors.Clear();
        mirrorErrorsView = NoMirrorErrors;
        Changed(nameof(TranscriptMirrorErrors));
    }

    // ---- moving the library (spec 8) ----------------------------------------------------------------------------

    /// <summary>
    /// Moves the library into <c>&lt;target&gt;\Wallfly</c> (macOS <c>moveLibrary</c>): only when nothing runs (recording,
    /// AI processing, speaker matching) and everything is saved. Everything except the app's own entries
    /// (<see cref="MeetingLibrary.StaysWithApp"/>, above all <c>Detection</c>) is copied, the copy is loaded, its audio paths
    /// are rebased and verified; only then the pointer is switched and the originals are deleted. On a failure only the copy
    /// is removed and the old library stays active. The loaded meetings stay the same objects (their audio paths are
    /// rebased in place).
    /// </summary>
    public async Task<bool> MoveLibraryAsync(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        if (Busy || closing || hooks.PostRecordingSteps.Any(step => step.HasPendingWork))
        {
            Error = CoreMessages.LibraryMoveWhileBusy;
            return false;
        }
        if (!Flush()) return false;
        string destination;
        try { destination = Path.GetFullPath(Path.Combine(target.Trim(), MeetingLibraryFolderName)); }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Error = CoreMessages.LibraryNotMoved(failure.Message);
            return false;
        }
        var source = Library;
        IsMovingLibrary = true;
        Notify();
        try
        {
            source.SaveSettings(Settings);
            var expected = Meetings.Select(meeting => meeting.Id).ToList();
            var copyStarted = DateTime.UtcNow.AddSeconds(-2);
            var move = await Task.Run(() => LibraryMove.Prepare(source, destination, expected)).ConfigureAwait(true);
            try { pointer.Remember(move.Destination.Root); }
            catch
            {
                try { Directory.Delete(move.Destination.Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
            // Edits saved to the old library while it was copied (task checkboxes, AI results) are written again below.
            var oldWriter = Writer;
            oldWriter.Flush(FlushTimeout);
            var rewritten = Meetings.Where(meeting => WrittenSince(source, meeting.Id, copyStarted)).Select(meeting => meeting.Id).ToHashSet();
            oldWriter.Dispose();
            Library = move.Destination;
            Writer = CreateWriter(move.Destination);
            foreach (var meeting in Meetings)
            {
                if (move.RebaseAudio(meeting) || rewritten.Contains(meeting.Id)) MarkDirty(meeting);
            }
            Writer.Flush(FlushTimeout);
            try { Catalog = move.Destination.LoadFolders(); } catch (Exception) { }
            Changed(nameof(Library));
            Status = CoreMessages.LibraryMoved;
            try { await Task.Run(move.RemoveOriginals).ConfigureAwait(true); }
            catch (IOException failure) { Error = CoreMessages.LibraryOriginalsNotDeleted(source.Root, failure.Message); }
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidDataException
            or System.Text.Json.JsonException or ArgumentException or NotSupportedException)
        {
            Error = CoreMessages.LibraryNotMoved(failure.Message);
            return false;
        }
        finally
        {
            IsMovingLibrary = false;
            Notify();
        }
    }

    /// <summary>The library folder created inside the chosen location.</summary>
    public const string MeetingLibraryFolderName = "Wallfly";

    private static bool WrittenSince(MeetingLibrary library, Guid meetingID, DateTime since)
    {
        try { return File.GetLastWriteTimeUtc(library.MeetingPath(meetingID)) >= since; }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException) { return true; }
    }
}
