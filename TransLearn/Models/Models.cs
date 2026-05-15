// ============================================================
// Models.cs
// 역할 : 앱 전체에서 사용하는 데이터 모델 정의.
//
// 주요 모델
//   TranslationRecord — 번역 기록 1건 (DB translations 테이블 행)
//   CaptureType       — 캡처 방식 열거형 (OCR=0 / Sound=1)
//   WordEntry         — 단어장 항목 1개 (DB learned_words 행)
//                       LearnPriority: 퀴즈 출제 가중치 (높을수록 자주 등장)
//                       UserScore: ThumbsUp - ThumbsDown
//   QuizItem          — 퀴즈 문제 1개 (DB 저장 안 함, 런타임 생성)
// ============================================================
namespace TransLearn.Models;

public class TranslationRecord
{
    public long Id { get; set; }
    public DateTime CapturedAt { get; set; }
    public string OriginalText { get; set; } = "";
    public string Translated { get; set; } = "";
    public CaptureType CaptureType { get; set; }
    public string AppName { get; set; } = "";
    public double? QualityScore { get; set; }
    public bool IsLearned { get; set; }
    /// <summary>NLP 분석 완료 여부 — 한 번 분석된 문장은 재분석 안 함</summary>
    public bool IsAnalyzed { get; set; }

    public string CaptureTypeLabel => CaptureType == CaptureType.OCR ? "🖥 OCR" : "🔊 Sound";
    public string DateLabel => CapturedAt.ToString("yyyy-MM-dd");
    public string TimeLabel => CapturedAt.ToString("HH:mm:ss");
}

public enum CaptureType { OCR = 0, Sound = 1 }

public class WordEntry
{
    public long Id { get; set; }
    public string Lemma { get; set; } = "";
    public string Pos { get; set; } = "";
    public int Frequency { get; set; }
    public double GdexScore { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string DateLabel => CreatedAt.ToString("yyyy-MM-dd HH:mm");

    public string ExampleSentence { get; set; } = "";
    public string? ExampleTranslated { get; set; }

    /// <summary>사용자 👍 누적 횟수</summary>
    public int ThumbsUp { get; set; }
    /// <summary>사용자 👎 누적 횟수</summary>
    public int ThumbsDown { get; set; }

    /// <summary>
    /// 순 평가 점수 = ThumbsUp - ThumbsDown.
    /// 양수 → 중요도 높음, 음수 → 중요도 낮음.
    /// </summary>
    public int UserScore => ThumbsUp - ThumbsDown;

    /// <summary>
    /// 학습 우선순위 가중치 (높을수록 퀴즈에 자주 등장).
    /// = Frequency * max(0.3, 1.0 - 0.15*UserScore)
    /// </summary>
    public double LearnPriority =>
        Frequency * Math.Max(0.1, 1.0 + 0.3 * UserScore);

    public string PosLabel => Pos switch
    {
        "NOUN" => "명사",
        "VERB" => "동사",
        "ADJ" => "형용사",
        "ADV" => "부사",
        _ => Pos
    };

    /// <summary>UI 표시용 평가 요약 문자열</summary>
    public string RatingLabel =>
        (ThumbsUp == 0 && ThumbsDown == 0) ? "미평가" :
        UserScore > 0 ? $"👍 {UserScore:+#;-#;0}" :
        UserScore < 0 ? $"👎 {UserScore:+#;-#;0}" :
                         "±0";
}

public class QuizItem
{
    public long WordId { get; set; }   // 평가 반영용
    public string Question { get; set; } = "";
    public string Sentence { get; set; } = "";
    public string Translation { get; set; } = "";  // ← 이게 추가됨
    public string Correct { get; set; } = "";
    public List<string> Choices { get; set; } = new();
    public string Pos { get; set; } = "";
    public bool? UserAnswer { get; set; }
}

public class MemoItem
{
    public long Id { get; set; }

    // 사용자가 메모한 영어 단어 또는 문장
    public string Content { get; set; } = "";

    // 사용자가 자유롭게 적는 설명
    public string Description { get; set; } = "";

    // 작성/수정 날짜를 하나로 관리
    public DateTime MemoDate { get; set; } = DateTime.Now;

    public string DateLabel => MemoDate.ToString("yyyy-MM-dd HH:mm");
}