using System.Globalization;
using System.Speech.Recognition;
using System.Speech.Synthesis;

namespace BoatDashboard;

/// <summary>
/// Fully offline voice for the assistant: Windows speech recognition (dictation grammar,
/// default microphone) for input and Windows TTS for spoken replies. No internet needed —
/// right for an onboard PC at sea. One utterance per activation (push-to-talk style).
/// </summary>
public sealed class VoiceService : IDisposable
{
    private SpeechRecognitionEngine? _rec;
    private readonly SpeechSynthesizer _tts = new();
    private bool _listening;

    /// <summary>Fired with the recognized text after a successful utterance.</summary>
    public event Action<string>? OnHeard;

    /// <summary>Fired when listening starts/stops (drives the mic button state).</summary>
    public event Action<bool>? OnListeningChanged;

    public VoiceService()
    {
        _tts.Rate = 1;
        try { _tts.SelectVoiceByHints(VoiceGender.Female, VoiceAge.Adult); } catch { }
    }

    /// <summary>True if a microphone + recognizer are available on this PC.</summary>
    public bool TryStartListening(out string? error)
    {
        error = null;
        if (_listening) return true;
        try
        {
            if (_rec is null)
            {
                _rec = new SpeechRecognitionEngine(CultureInfo.GetCultureInfo("en-US"));
                _rec.LoadGrammar(new DictationGrammar());
                _rec.SetInputToDefaultAudioDevice();   // throws if no microphone
                _rec.SpeechRecognized += (_, e) =>
                {
                    if (e.Result?.Text is { Length: > 0 } text)
                    {
                        StopListening();
                        OnHeard?.Invoke(text);
                    }
                };
                _rec.RecognizeCompleted += (_, _) => SetListening(false);
            }
            _rec.RecognizeAsync(RecognizeMode.Single);   // one utterance, then stops
            SetListening(true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void StopListening()
    {
        try { _rec?.RecognizeAsyncCancel(); } catch { }
        SetListening(false);
    }

    private void SetListening(bool v)
    {
        if (_listening == v) return;
        _listening = v;
        OnListeningChanged?.Invoke(v);
    }

    /// <summary>Speaks the reply aloud (async, non-blocking). Trims long text for speech.</summary>
    public void Speak(string text)
    {
        try
        {
            _tts.SpeakAsyncCancelAll();
            var spoken = text.Length > 400 ? text[..400] : text;
            _tts.SpeakAsync(spoken);
        }
        catch { /* no audio output — non-fatal */ }
    }

    public void Dispose()
    {
        try { _rec?.Dispose(); } catch { }
        try { _tts.Dispose(); } catch { }
    }
}
