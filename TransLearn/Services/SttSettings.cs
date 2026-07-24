// ============================================================
// SttSettings.cs
// 역할 : STT 문장 끊기(세그먼트) 및 자막 표시 관련 사용자 설정.
//        SecureKeyStorage 에 영속 저장한다 (provider, azure_region 과 동일한 방식).
//
// 설정 항목
//   SilenceTimeoutMs     — 이만큼 조용하면 문장을 확정한다. 짧을수록 자막이 자주 갱신된다.
//   MaxSegmentMs         — 침묵이 없어도 이 시간이 지나면 강제로 끊는다. 0 이면 사용하지 않음.
//   SubtitleMaxSentences — 자막 레이어에 한 번에 표시할 최대 문장 수.
//
//  ※ SilenceTimeoutMs / MaxSegmentMs 는 SpeechRecognizer 를 만들 때 적용되므로
//    값을 바꾼 뒤에는 sound 번역을 중지했다가 다시 시작해야 반영된다.
//    SubtitleMaxSentences 는 즉시 반영된다.
// ============================================================
using System;

namespace TransLearn.Services;

public static class SttSettings
{
    // ── 기본값 ────────────────────────────────────────────────────────────
    public const int DefaultSilenceTimeoutMs = 500;
    public const int DefaultMaxSegmentMs = 0;     // 0 = 사용 안 함
    public const int DefaultSubtitleMaxSentences = 2;

    // ── 허용 범위 ─────────────────────────────────────────────────────────
    public const int MinSilenceTimeoutMs = 200;   // 이보다 짧으면 단어 중간에 잘린다
    public const int MaxSilenceTimeoutMs = 2000;
    public const int MinMaxSegmentMs = 2000;  // 0 은 예외적으로 허용 (사용 안 함)
    public const int MaxMaxSegmentMs = 15000;

    private static int _silenceTimeoutMs = DefaultSilenceTimeoutMs;
    private static int _maxSegmentMs = DefaultMaxSegmentMs;
    private static int _subtitleMaxSentences = DefaultSubtitleMaxSentences;
    private static bool _loaded;

    /// <summary>침묵이 이만큼 이어지면 문장을 확정한다 (밀리초).</summary>
    public static int SilenceTimeoutMs
    {
        get { EnsureLoaded(); return _silenceTimeoutMs; }
        set => _silenceTimeoutMs = Math.Clamp(value, MinSilenceTimeoutMs, MaxSilenceTimeoutMs);
    }

    /// <summary>침묵이 없어도 이 시간이 지나면 강제로 끊는다. 0 이면 사용하지 않는다 (밀리초).</summary>
    public static int MaxSegmentMs
    {
        get { EnsureLoaded(); return _maxSegmentMs; }
        set
        {
            if (value <= 0) { _maxSegmentMs = 0; return; }
            _maxSegmentMs = Math.Clamp(value, MinMaxSegmentMs, MaxMaxSegmentMs);
        }
    }

    /// <summary>자막 레이어에 한 번에 표시할 최대 문장 수.</summary>
    public static int SubtitleMaxSentences
    {
        get { EnsureLoaded(); return _subtitleMaxSentences; }
        set => _subtitleMaxSentences = Math.Clamp(value, 1, 5);
    }

    // ── 저장 / 불러오기 ───────────────────────────────────────────────────

    private static void EnsureLoaded()
    {
        if (!_loaded) Load();
    }

    public static void Load()
    {
        _loaded = true;   // 재진입 방지를 위해 먼저 세운다

        _silenceTimeoutMs = ReadInt("stt_silence_ms", DefaultSilenceTimeoutMs);
        _maxSegmentMs = ReadInt("stt_maxseg_ms", DefaultMaxSegmentMs);
        _subtitleMaxSentences = ReadInt("subtitle_max_sent", DefaultSubtitleMaxSentences);

        // 범위 보정
        _silenceTimeoutMs = Math.Clamp(_silenceTimeoutMs, MinSilenceTimeoutMs, MaxSilenceTimeoutMs);
        _maxSegmentMs = _maxSegmentMs <= 0 ? 0 : Math.Clamp(_maxSegmentMs, MinMaxSegmentMs, MaxMaxSegmentMs);
        _subtitleMaxSentences = Math.Clamp(_subtitleMaxSentences, 1, 5);

        System.Diagnostics.Debug.WriteLine(
            $"[SttSettings] 침묵={_silenceTimeoutMs}ms, 최대세그먼트={_maxSegmentMs}ms, 자막문장수={_subtitleMaxSentences}");
    }

    public static void Save()
    {
        SecureKeyStorage.Save("stt_silence_ms", SilenceTimeoutMs.ToString());
        SecureKeyStorage.Save("stt_maxseg_ms", MaxSegmentMs.ToString());
        SecureKeyStorage.Save("subtitle_max_sent", SubtitleMaxSentences.ToString());

        System.Diagnostics.Debug.WriteLine(
            $"[SttSettings] 저장 — 침묵={SilenceTimeoutMs}ms, 최대세그먼트={MaxSegmentMs}ms, 자막문장수={SubtitleMaxSentences}");
    }

    public static void ResetToDefault()
    {
        _silenceTimeoutMs = DefaultSilenceTimeoutMs;
        _maxSegmentMs = DefaultMaxSegmentMs;
        _subtitleMaxSentences = DefaultSubtitleMaxSentences;
        _loaded = true;
        Save();
    }

    private static int ReadInt(string key, int fallback)
    {
        try
        {
            if (!SecureKeyStorage.Exists(key)) return fallback;
            var raw = SecureKeyStorage.Load(key);
            return int.TryParse(raw, out var v) ? v : fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
