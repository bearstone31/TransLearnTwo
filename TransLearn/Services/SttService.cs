// ============================================================
// SttService.cs
// 역할 : 음성 인식(STT) 서비스 퍼사드.
//        설정에 따라 Windows 내장 STT 또는 Azure STT 엔진을
//        동적으로 생성·교체하며 상위에는 동일 이벤트 인터페이스 노출.
//
// 동작 흐름
//   SoundViewModel.StartAsync()
//     → SttService.StartAsync()
//       → SttEngineType 확인 → ISttEngine 구현체 생성
//       → ISttEngine.StartAsync() (엔진별 초기화)
//     ← SentenceRecognized / Recognizing / Error 이벤트로 결과 전달
//
// 엔진 종류
//   Windows : System.Speech.Recognition — 무료·오프라인, 영어 언어팩 필요
//   Azure   : CognitiveServices.Speech  — 클라우드, 월 5시간 무료 티어
// ============================================================
using System.Globalization;
using System.Speech.Recognition;          // Windows 내장 STT (SpeechRecognitionEngine 등)
using System.Threading.Channels;
using System.IO;
using System.Speech.AudioFormat;
// Azure STT 네임스페이스는 alias로 분리 — System.Speech.Recognition.SpeechRecognizer와
// Microsoft.CognitiveServices.Speech.SpeechRecognizer 의 모호한 참조를 방지
using AzureSpeech      = Microsoft.CognitiveServices.Speech;
using AzureSpeechAudio = Microsoft.CognitiveServices.Speech.Audio;

namespace TransLearn.Services;

// ── 엔진 종류 열거형 ──────────────────────────────────────────────────────────
/// <summary>음성 인식 엔진 종류. SecureKeyStorage "stt_engine" 키로 영속 저장.</summary>
public enum SttEngineType { Windows, Azure }

// ── 퍼사드 ───────────────────────────────────────────────────────────────────
/// <summary>
/// STT 서비스 진입점. 엔진 종류에 따라 내부 구현체를 교체하지만
/// SoundViewModel에서는 동일한 이벤트로 결과를 받는다.
/// </summary>
public class SttService : IAsyncDisposable
{
    private ISttEngine? _activeEngine;

    public event Action<string>? SentenceRecognized;  // 완성 문장 → 번역 트리거
    public event Action<string>? Recognizing;          // 중간 결과 → 실시간 자막
    public event Action<string>? Error;                // 오류 메시지 전달

    public bool IsRunning => _activeEngine?.IsRunning ?? false;

    /// <summary>
    /// 현재 엔진으로 시작 가능 여부.
    /// Windows: 항상 true (실패는 StartAsync에서 알림)
    /// Azure: azure_key + azure_region 저장 여부
    /// </summary>
    public bool IsConfigured =>
        SelectedEngine == SttEngineType.Windows
        || (SecureKeyStorage.Exists("azure_key") && SecureKeyStorage.Exists("azure_region"));

    // ── 엔진 선택 (영속 저장) ────────────────────────────────────────────────
    public static SttEngineType SelectedEngine
    {
        get => SecureKeyStorage.Load("stt_engine") == "Azure"
               ? SttEngineType.Azure : SttEngineType.Windows;
        set => SecureKeyStorage.Save("stt_engine",
               value == SttEngineType.Azure ? "Azure" : "Windows");
    }

    /// <summary>엔진 설정 변경 시 SoundViewModel에서 구독해 UI 갱신에 사용</summary>
    public static event Action? EngineChanged;
    public static void NotifyEngineChanged() => EngineChanged?.Invoke();

    // ── 시작 ─────────────────────────────────────────────────────────────────
    public async Task StartAsync()
    {
        if (IsRunning) return;

        _activeEngine = SelectedEngine == SttEngineType.Azure
            ? (ISttEngine)new AzureSttEngine()
            : new WindowsSttEngine();

        _activeEngine.SentenceRecognized += s => SentenceRecognized?.Invoke(s);
        _activeEngine.Recognizing        += s => Recognizing?.Invoke(s);
        _activeEngine.Error              += s => Error?.Invoke(s);

        await _activeEngine.StartAsync();
    }

    /// <summary>AudioCaptureService 원시 오디오 → 활성 엔진에 전달</summary>
    public void FeedAudio(byte[] rawChunk, NAudio.Wave.WaveFormat srcFormat)
        => _activeEngine?.FeedAudio(rawChunk, srcFormat);

    public async Task StopAsync()
    {
        if (_activeEngine == null) return;
        await _activeEngine.StopAsync();
        if (_activeEngine is IDisposable d) d.Dispose();
        _activeEngine = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}

// ═════════════════════════════════════════════════════════════════════════════
// 내부 인터페이스
// ═════════════════════════════════════════════════════════════════════════════
internal interface ISttEngine
{
    event Action<string>? SentenceRecognized;
    event Action<string>? Recognizing;
    event Action<string>? Error;
    bool IsRunning { get; }
    Task StartAsync();
    void FeedAudio(byte[] rawChunk, NAudio.Wave.WaveFormat srcFormat);
    Task StopAsync();
}

// ═════════════════════════════════════════════════════════════════════════════
// ① Windows 내장 STT 엔진
// ═════════════════════════════════════════════════════════════════════════════
/// <summary>
/// System.Speech.Recognition 기반 Windows 내장 오프라인 STT.
/// 오디오 파이프라인:
///   WASAPI float32/stereo → PCM 16kHz 16bit Mono → BlockingAudioStream
///   → SpeechRecognitionEngine (별도 스레드에서 블로킹 Read)
/// </summary>
internal sealed class WindowsSttEngine : ISttEngine, IDisposable
{
    public event Action<string>? SentenceRecognized;
    public event Action<string>? Recognizing;
    public event Action<string>? Error;

    private SpeechRecognitionEngine? _engine;
    private BlockingAudioStream?     _stream;
    private bool _isRunning;

    public bool IsRunning => _isRunning;

    public Task StartAsync()
    {
        if (_isRunning) return Task.CompletedTask;
        try
        {
            // 영어(미국) 엔진 초기화 — 언어팩 없으면 예외
            _engine = new SpeechRecognitionEngine(new CultureInfo("en-US"));
            _engine.LoadGrammar(new DictationGrammar()); // 자유 형식 받아쓰기

            _engine.SpeechRecognized += (_, e) =>
            {
                var t = e.Result?.Text;
                if (!string.IsNullOrWhiteSpace(t)) SentenceRecognized?.Invoke(t);
            };
            _engine.SpeechHypothesized += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Result?.Text)) Recognizing?.Invoke(e.Result.Text);
            };
            _engine.RecognizeCompleted += (_, e) =>
            {
                if (e.Error != null) Error?.Invoke($"Windows STT 오류: {e.Error.Message}");
            };

            // PCM 16kHz 16bit Mono 스트림 연결
            _stream = new BlockingAudioStream();
            var fmt = new SpeechAudioFormatInfo(
                EncodingFormat.Pcm, 16000, 16, 1, 32000, 2, null);
            _engine.SetInputToAudioStream(_stream, fmt);
            _engine.RecognizeAsync(RecognizeMode.Multiple); // 비동기 지속 인식
            _isRunning = true;
        }
        catch (Exception ex)
        {
            Error?.Invoke(
                $"⚠️ Windows STT 초기화 실패: {ex.Message}\n\n" +
                "해결 방법:\n" +
                "Windows 설정 → 시간 및 언어 → 음성 →\n" +
                "음성 언어를 'English (United States)'로 설정");
        }
        return Task.CompletedTask;
    }

    public void FeedAudio(byte[] rawChunk, NAudio.Wave.WaveFormat srcFormat)
    {
        if (!_isRunning || _stream == null) return;
        _stream.Push(AudioConverter.ToPcm16k(rawChunk, srcFormat));
    }

    public Task StopAsync()
    {
        if (!_isRunning) return Task.CompletedTask;
        _stream?.Complete();
        try { _engine?.RecognizeAsyncStop(); } catch { }
        _isRunning = false;
        return Task.CompletedTask;
    }

    public void Dispose() { _engine?.Dispose(); _stream?.Dispose(); }
}

// ═════════════════════════════════════════════════════════════════════════════
// ② Azure STT 엔진
// ═════════════════════════════════════════════════════════════════════════════
/// <summary>
/// Azure Cognitive Services Speech SDK 기반 클라우드 STT.
/// 오디오 파이프라인:
///   WASAPI float32/stereo → PCM 16kHz 16bit Mono
///   → Channel(byte[]) → PushAudioInputStream → Azure 클라우드
/// </summary>
internal sealed class AzureSttEngine : ISttEngine, IDisposable
{
    public event Action<string>? SentenceRecognized;
    public event Action<string>? Recognizing;
    public event Action<string>? Error;

    // 모든 Azure 타입에 AzureSpeech./AzureSpeechAudio. 접두사 사용
    // → System.Speech.Recognition.SpeechRecognizer 와의 모호한 참조 방지
    private AzureSpeech.SpeechRecognizer?        _recognizer;
    private AzureSpeechAudio.PushAudioInputStream? _pushStream;
    private Channel<byte[]>?                       _audioChannel;
    private CancellationTokenSource?               _cts;
    private bool _isRunning;

    public bool IsRunning => _isRunning;

    public async Task StartAsync()
    {
        if (_isRunning) return;

        var key    = SecureKeyStorage.Load("azure_key");
        var region = SecureKeyStorage.Load("azure_region");
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(region))
        {
            Error?.Invoke("Azure STT 키 또는 리전이 설정되지 않았습니다. 환경설정에서 입력해 주세요.");
            return;
        }

        _audioChannel = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        _pushStream   = AzureSpeechAudio.AudioInputStream.CreatePushStream(
                            AzureSpeechAudio.AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));

        var cfg = AzureSpeech.SpeechConfig.FromSubscription(key, region);
        cfg.SpeechRecognitionLanguage = "en-US";
        cfg.SetProperty(AzureSpeech.PropertyId.Speech_SegmentationSilenceTimeoutMs, "1500");
        cfg.SetProperty(AzureSpeech.PropertyId.SpeechServiceResponse_PostProcessingOption, "TrueText");

        _recognizer = new AzureSpeech.SpeechRecognizer(
                          cfg, AzureSpeechAudio.AudioConfig.FromStreamInput(_pushStream));
        _recognizer.Recognizing += (_, e) => Recognizing?.Invoke(e.Result.Text);
        _recognizer.Recognized  += (_, e) =>
        {
            if (e.Result.Reason == AzureSpeech.ResultReason.RecognizedSpeech
                && !string.IsNullOrWhiteSpace(e.Result.Text))
                SentenceRecognized?.Invoke(e.Result.Text);
        };
        _recognizer.Canceled += (_, e) =>
        {
            if (e.Reason == AzureSpeech.CancellationReason.Error)
                Error?.Invoke($"Azure STT 오류 [{e.ErrorCode}]: {e.ErrorDetails}");
        };

        await _recognizer.StartContinuousRecognitionAsync();

        _cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var chunk in _audioChannel.Reader.ReadAllAsync(_cts.Token))
                    _pushStream?.Write(chunk);
            }
            catch (OperationCanceledException) { }
        }, _cts.Token);

        _isRunning = true;
    }

    public void FeedAudio(byte[] rawChunk, NAudio.Wave.WaveFormat srcFormat)
    {
        if (!_isRunning || _audioChannel == null) return;
        _audioChannel.Writer.TryWrite(AudioConverter.ToPcm16k(rawChunk, srcFormat));
    }

    public async Task StopAsync()
    {
        if (!_isRunning) return;
        _cts?.Cancel();
        _audioChannel?.Writer.Complete();
        if (_recognizer != null) await _recognizer.StopContinuousRecognitionAsync();
        _isRunning = false;
    }

    public void Dispose() { _recognizer?.Dispose(); _pushStream?.Dispose(); _cts?.Dispose(); }
}

// ═════════════════════════════════════════════════════════════════════════════
// 공통 오디오 변환기
// ═════════════════════════════════════════════════════════════════════════════
/// <summary>
/// WASAPI Loopback 포맷(IEEE Float 32bit, 스테레오)을
/// STT가 요구하는 PCM 16kHz 16bit Mono로 변환하는 유틸리티.
/// NAudio MediaFoundationResampler로 리샘플링 + 채널 다운믹스.
/// </summary>
internal static class AudioConverter
{
    public static byte[] ToPcm16k(byte[] src, NAudio.Wave.WaveFormat srcFmt)
    {
        using var ms   = new System.IO.MemoryStream(src);
        using var r    = new NAudio.Wave.RawSourceWaveStream(ms, srcFmt);
        using var conv = new NAudio.Wave.MediaFoundationResampler(r,
                             new NAudio.Wave.WaveFormat(16000, 16, 1));
        using var dst  = new System.IO.MemoryStream();
        var buf = new byte[4096]; int n;
        while ((n = conv.Read(buf, 0, buf.Length)) > 0) dst.Write(buf, 0, n);
        return dst.ToArray();
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// BlockingAudioStream (Windows STT 전용)
// ═════════════════════════════════════════════════════════════════════════════
/// <summary>
/// SpeechRecognitionEngine 전용 오디오 주입 스트림.
/// Push(byte[])로 PCM 청크를 주입하면 엔진의 블로킹 Read()가 즉시 소비.
/// Complete() 호출 후 큐가 비면 Read()가 0(EOF)을 반환해 엔진이 중지.
/// </summary>
internal sealed class BlockingAudioStream : Stream
{
    private readonly System.Collections.Concurrent.BlockingCollection<byte[]>
        _q = new(boundedCapacity: 300);
    private byte[]? _cur; private int _off;

    public override bool CanRead  => true;
    public override bool CanSeek  => false;
    public override bool CanWrite => false;
    public override long Length   => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public void Push(byte[] data) { if (!_q.IsAddingCompleted) _q.TryAdd(data, 80); }
    public void Complete() => _q.CompleteAdding();

    public override int Read(byte[] buf, int offset, int count)
    {
        int w = 0;
        while (w < count)
        {
            if (_cur == null || _off >= _cur.Length)
            {
                if (_q.IsCompleted && _q.Count == 0) return w;
                byte[]? n;
                while (!_q.TryTake(out n, 50)) { if (_q.IsCompleted) return w; }
                _cur = n!; _off = 0;
            }
            int c = Math.Min(count - w, _cur.Length - _off);
            Buffer.BlockCopy(_cur, _off, buf, offset + w, c);
            _off += c; w += c;
        }
        return w;
    }

    public override void Flush() { }
    public override long   Seek(long o, SeekOrigin orig) => throw new NotSupportedException();
    public override void   SetLength(long v)             => throw new NotSupportedException();
    public override void   Write(byte[] b, int o, int c) => throw new NotSupportedException();
    protected override void Dispose(bool d) { if (d) _q.Dispose(); base.Dispose(d); }
}
