// ============================================================
// AudioCaptureService.cs
// 역할 : Windows WASAPI Loopback 오디오 캡처 서비스.
//        시스템 재생음(스피커 출력)을 실시간으로 캡처하고,
//        - DataAvailable   : STT에 전달할 원시 오디오 청크
//        - AudioLevelChanged: VU 미터 UI용 RMS 레벨(0.0~1.0)
//        두 가지 이벤트로 외부에 노출한다.
//
// 동작 흐름
//   Start() → WasapiLoopbackCapture.StartRecording()
//     DataAvailable 이벤트 발생 (약 100ms 간격)
//       → DataAvailable(chunk, WaveFormat)  → SttService.FeedAudio()
//       → AudioLevelChanged(0.0~1.0)        → SoundViewModel.AudioLevel
// ============================================================
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TransLearn.Services;

public class AudioCaptureService : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private bool _isRunning;

    /// <summary>원시 오디오 청크 (STT 파이프라인으로 전달)</summary>
    public event Action<byte[], WaveFormat>? DataAvailable;

    /// <summary>
    /// RMS 오디오 레벨 (0.0 = 무음, 1.0 = 최대 음량).
    /// VU 미터 UI 갱신용. 약 30ms마다 발생 (Display 주사율 고려).
    /// </summary>
    public event Action<double>? AudioLevelChanged;

    public bool IsRunning => _isRunning;

    // RMS 갱신 주기 제한 (30fps) — WPF Dispatcher 부하 방지
    private DateTime _lastLevelUpdate = DateTime.MinValue;
    private const double LevelUpdateMs = 30;

    public WaveFormat? CaptureFormat => _capture?.WaveFormat;

    // ── 시작 ─────────────────────────────────────────────────────────────────
    public void Start()
    {
        if (_isRunning) return;

        _cts     = new CancellationTokenSource();
        _capture = new WasapiLoopbackCapture();

        _capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded <= 0) return;

            // STT에 전달할 청크 복사
            var chunk = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);
            DataAvailable?.Invoke(chunk, _capture.WaveFormat);

            // VU 미터: 30fps 제한으로 RMS 계산 후 이벤트 발생
            var now = DateTime.UtcNow;
            if ((now - _lastLevelUpdate).TotalMilliseconds >= LevelUpdateMs)
            {
                _lastLevelUpdate = now;
                var level = CalculateRms(e.Buffer, e.BytesRecorded, _capture.WaveFormat);
                AudioLevelChanged?.Invoke(level);
            }
        };

        _capture.RecordingStopped += (_, e) =>
        {
            _isRunning = false;
            if (e.Exception != null)
                System.Diagnostics.Debug.WriteLine(
                    $"[AudioCapture] 중지됨: {e.Exception.Message}");
        };

        _capture.StartRecording();
        _isRunning = true;
    }

    // ── 정지 ─────────────────────────────────────────────────────────────────
    public void Stop()
    {
        _cts?.Cancel();
        _capture?.StopRecording();
        _isRunning = false;
        // VU 미터 0으로 초기화
        AudioLevelChanged?.Invoke(0.0);
    }

    // ── RMS 레벨 계산 ─────────────────────────────────────────────────────────
    /// <summary>
    /// WASAPI 캡처 버퍼(IEEE Float 32bit)로부터 RMS 값을 계산해 0.0~1.0으로 반환.
    /// RMS = sqrt(sum(x²) / n). 가시성을 위해 6배 부스트 후 클리핑.
    /// PCM 포맷(int16)의 경우 0.0 반환 (변환 불필요).
    /// </summary>
    private static double CalculateRms(byte[] buffer, int bytesRecorded, WaveFormat fmt)
    {
        // WASAPI Loopback은 통상 IEEE Float 32bit
        if (fmt.Encoding != WaveFormatEncoding.IeeeFloat) return 0.0;

        int bytesPerSample = fmt.BitsPerSample / 8; // float32 = 4바이트
        int sampleCount    = bytesRecorded / bytesPerSample;
        if (sampleCount == 0) return 0.0;

        double sumSq = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            float s = BitConverter.ToSingle(buffer, i * bytesPerSample);
            sumSq += s * s;
        }

        double rms = Math.Sqrt(sumSq / sampleCount);
        // 시각적 가시성을 위해 6배 증폭 후 1.0 클리핑
        return Math.Min(1.0, rms * 6.0);
    }

    // ── 해제 ─────────────────────────────────────────────────────────────────
    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _capture?.Dispose();
        _cts?.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
