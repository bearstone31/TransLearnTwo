"""
TransLearn NLP Analyzer
Usage:
    nlp_analyzer.exe analyze  < sentences_json
    nlp_analyzer.exe quiz N D < word_entries_json
        N = 문제 수 (기본 10)
        D = 난이도 0.0~1.0 (기본 0.5)
"""
import os
os.environ["USE_TORCH"] = "0"
os.environ["USE_TF"] = "0"
os.environ["USE_JAX"] = "0"

import sys
import json
import re
import random
from collections import Counter

# ── 경로 설정 ────────────────────────────────────────────────────────────
def _get_base_path():
    if getattr(sys, 'frozen', False):
        return sys._MEIPASS
    return os.path.dirname(os.path.abspath(__file__))

BASE_PATH = _get_base_path()

# ── spaCy 초기화 ─────────────────────────────────────────────────────────
SPACY_AVAILABLE = False
nlp = None

try:
    import spacy
    model_path = os.path.join(BASE_PATH, "en_core_web_sm")
    if os.path.exists(model_path):
        nlp = spacy.load(model_path)
    else:
        nlp = spacy.load("en_core_web_sm")
    SPACY_AVAILABLE = True
except Exception as e:
    sys.stderr.write(f"[NLP] spaCy load failed: {e}\n")

STOPWORDS = nlp.Defaults.stop_words if SPACY_AVAILABLE and nlp else set()
TARGET_POS = {"VERB", "ADJ", "ADV"}

# ── NLTK 초기화 ──────────────────────────────────────────────────────────
NLTK_AVAILABLE = False
try:
    import nltk
    from nltk import pos_tag, word_tokenize
    NLTK_AVAILABLE = True
    sys.stderr.write("[NLP] NLTK 로드 성공\n")
except Exception as e:
    sys.stderr.write(f"[NLP] NLTK load failed: {e}\n")

# NLTK POS 태그 → 허용 여부
# 허용: VB*(동사), JJ*(형용사), RB*(부사), MD(조동사)
# 제외: NN*(명사), PRP*(대명사), DT(관사), IN(전치사), CC(접속사) 등
ALLOWED_NLTK_POS = {
    "VB", "VBD", "VBG", "VBN", "VBP", "VBZ",  # 동사
    "JJ", "JJR", "JJS",                          # 형용사
    "RB", "RBR", "RBS",                          # 부사
    "MD",                                         # 조동사 (will, can, should 등)
}

# ── 퀴즈 마스크 단어 제외 목록 (NLTK 없을 때 폴백) ───────────────────────
EXCLUDED_MASK_WORDS = {
    "i", "me", "my", "mine", "myself",
    "you", "your", "yours", "yourself", "yourselves",
    "he", "him", "his", "himself",
    "she", "her", "hers", "herself",
    "it", "its", "itself",
    "we", "us", "our", "ours", "ourselves",
    "they", "them", "their", "theirs", "themselves",
    "this", "that", "these", "those",
    "who", "whom", "whose", "which", "what", "whoever", "whatever",
    "a", "an", "the",
    "in", "on", "at", "by", "for", "with", "about", "against",
    "between", "through", "during", "before", "after", "above",
    "below", "to", "from", "up", "down", "of", "off", "over",
    "under", "into", "onto", "upon", "within", "without", "toward",
    "towards", "beside", "besides", "beyond", "except", "per",
    "via", "versus", "among", "amongst", "amid", "amidst",
    "and", "or", "but", "nor", "so", "yet", "for",
    "although", "because", "since", "unless", "until", "while",
    "if", "though", "even", "whereas", "whenever", "wherever",
    "whether", "after", "before", "when", "where", "as",
    "not", "no", "neither", "nor",
    "thing", "things", "way", "ways", "time", "times",
    "year", "years", "day", "days", "week", "weeks", "month", "months",
    "man", "men", "woman", "women", "people", "person", "persons",
    "place", "places", "part", "parts", "case", "cases",
    "number", "numbers", "point", "points", "hand", "hands",
    "world", "life", "home", "house", "room", "word", "words",
    "lot", "lots", "kind", "sort", "type", "types",
    "one", "two", "three", "four", "five", "six", "seven", "eight",
    "nine", "ten", "first", "second", "third",
}


# ── NLTK POS 태깅으로 단어 품사 판별 ────────────────────────────────────
def get_nltk_pos(word: str, sentence: str) -> str:
    """문장 내에서 단어의 NLTK POS 태그 반환"""
    if not NLTK_AVAILABLE:
        return "UNKNOWN"


    try:
        tokens = word_tokenize(sentence)
        tagged = pos_tag(tokens)
        for token, tag in tagged:
            if token.lower() == word.lower():
                return tag
        # 단어 단독으로 태깅
        tagged_single = pos_tag([word])
        return tagged_single[0][1] if tagged_single else "UNKNOWN"
    except Exception:
        return "UNKNOWN"


# ── 퀴즈 대상 단어 적합성 판별 ───────────────────────────────────────────
def is_valid_mask_word(lemma: str, pos: str, example_sentence: str) -> bool:
    """
    퀴즈 빈칸(MASK)으로 선택될 단어가 적합한지 판별
    동사, 형용사, 부사, 조동사만 허용
    명사, 대명사, 관사, 전치사 등 제외
    """
    lemma_lower = lemma.lower()

    # 너무 짧은 단어 제외
    if len(lemma_lower) <= 2:
        return False

    # 제외 목록 체크
    if lemma_lower in EXCLUDED_MASK_WORDS:
        return False

    # spaCy POS 태그 활용
    if pos in ("NOUN", "PROPN", "PRON", "DET", "ADP", "CCONJ", "SCONJ",
               "PART", "NUM", "PUNCT", "SPACE", "SYM", "X"):
        return False
    if pos in ("VERB", "ADJ", "ADV", "AUX"):
        return True

    # NLTK POS 태깅으로 판별 (spaCy 없을 때)
    if NLTK_AVAILABLE and example_sentence:
        nltk_pos = get_nltk_pos(lemma, example_sentence)
        sys.stderr.write(f"[POS] {lemma} → NLTK: {nltk_pos}\n")

        if nltk_pos in ALLOWED_NLTK_POS:
            return True

        # 명사류 제외
        if nltk_pos.startswith("NN") or nltk_pos.startswith("PRP") or \
           nltk_pos in ("DT", "IN", "CC", "CD", "EX", "FW", "LS",
                        "PDT", "POS", "RP", "SYM", "TO", "UH", "WDT",
                        "WP", "WRB"):
            return False

        return False  # 불명확하면 제외

    # 고유명사 감지 (문장 중간 대문자)
    if example_sentence:
        words_in_sent = example_sentence.split()
        for i, w in enumerate(words_in_sent):
            clean_w = re.sub(r"[^a-zA-Z]", "", w).lower()
            if clean_w == lemma_lower and i > 0 and w[0].isupper():
                return False

    return False  # NLTK도 없고 POS도 모르면 제외


# ── myDistilBERT ONNX 로드 ───────────────────────────────────────────────
DISTILBERT_AVAILABLE = False
ort_session = None
hf_tokenizer = None

try:
    import onnxruntime as ort
    import numpy as np
    from transformers import DistilBertTokenizer

    onnx_path = os.path.join(BASE_PATH, "models", "my_distilbert.onnx")
    if not os.path.exists(onnx_path):
        onnx_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "models", "my_distilbert.onnx")

    if os.path.exists(onnx_path):
        ort_session  = ort.InferenceSession(onnx_path)
        tokenizer_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "tokenizer")
        if not os.path.exists(tokenizer_path):
            tokenizer_path = "distilbert-base-uncased"
        hf_tokenizer = DistilBertTokenizer.from_pretrained(tokenizer_path)
        DISTILBERT_AVAILABLE = True
        sys.stderr.write(f"[NLP] myDistilBERT 로드 성공: {onnx_path}\n")
    else:
        sys.stderr.write(f"[NLP] ONNX 파일 없음: {onnx_path}\n")
except Exception as e:
    sys.stderr.write(f"[NLP] myDistilBERT 로드 실패: {e}\n")


# ── ONNX 오답 생성 ───────────────────────────────────────────────────────
def get_distractors(sentence: str, correct: str, difficulty: float = 0.5, n: int = 3) -> list:
    if not DISTILBERT_AVAILABLE:
        return []

    FILTER_WORDS = {
        "the", "and", "or", "but", "in", "on", "at", "to", "for",
        "of", "a", "an", "is", "be", "are", "was", "were", "it",
        "as", "by", "from", "with", "that", "this", "not", "no",
        "so", "if", "do", "did", "let", "see", "get", "got",
        "define", "make", "use", "put", "set", "go", "may", "can",
        "will", "just", "also", "very", "more", "than", "then",
        "up", "out", "about", "into", "over", "after", "before",
        "has", "had", "have", "been", "being", "its", "my", "your",
        "he", "she", "we", "they", "who", "what", "which", "how",
        "when", "where", "there", "here", "all", "one", "two"
    }

    try:
        import numpy as np

        masked = re.sub(re.escape(correct), "[MASK]", sentence, count=1, flags=re.IGNORECASE)
        if "[MASK]" not in masked:
            masked = sentence + " [MASK]"

        inputs = hf_tokenizer(
            masked, return_tensors="np",
            max_length=64, padding="max_length", truncation=True
        )

        outputs = ort_session.run(None, {
            "input_ids":      inputs["input_ids"].astype(np.int64),
            "attention_mask": inputs["attention_mask"].astype(np.int64)
        })

        logits    = outputs[0]
        input_ids = inputs["input_ids"][0]
        mask_idx  = np.where(input_ids == hf_tokenizer.mask_token_id)[0]

        if len(mask_idx) == 0:
            return []

        mask_logits = logits[0, mask_idx[0], :]
        exp_l = np.exp(mask_logits - np.max(mask_logits))
        probs = exp_l / exp_l.sum()

        top_ids = np.argsort(probs)[::-1][:100]
        candidates = []
        for idx in top_ids:
            word = hf_tokenizer.decode([idx]).strip()
            if (word.isalpha()
                    and word.lower() != correct.lower()
                    and len(word) > 2
                    and word.lower() not in FILTER_WORDS):
                candidates.append((word, probs[idx]))
            if len(candidates) >= 50:
                break

        if len(candidates) < n:
            return []

        if difficulty >= 0.9:
            pool = candidates[0:7]
        elif difficulty >= 0.6:
            pool = candidates[7:15]
        elif difficulty >= 0.3:
            pool = candidates[15:30]
        else:
            pool = candidates[39:50]

        if len(pool) < n:
            pool = candidates[:n + 1]

        selected = random.sample(pool, min(n, len(pool)))
        return [w for w, _ in selected]

    except Exception as e:
        sys.stderr.write(f"[NLP] distractor error: {e}\n")
        return []


# ── GDEX 점수 ────────────────────────────────────────────────────────────
def compute_gdex(doc) -> float:
    tokens = [t for t in doc if not t.is_punct and not t.is_space]
    n = len(tokens)
    if n == 0:
        return 0.0
    length_score     = max(0.0, 1.0 - abs(n - 18) / 20.0)
    stop_ratio       = sum(1 for t in tokens if t.lemma_.lower() in STOPWORDS) / n
    stop_score       = 1.0 - stop_ratio
    clause_count     = sum(1 for t in doc if t.dep_ in ("advcl", "relcl", "ccomp", "xcomp"))
    complexity_score = max(0.0, 1.0 - clause_count * 0.2)
    return round(length_score * 0.4 + stop_score * 0.3 + complexity_score * 0.3, 4)


# ── 문장 분석 ────────────────────────────────────────────────────────────
def analyze_sentences(sentences: list) -> list:
    if not sentences:
        return []
    if not SPACY_AVAILABLE:
        return simple_frequency(sentences)

    try:
        docs = list(nlp.pipe(sentences, batch_size=32))
    except Exception as e:
        sys.stderr.write(f"[NLP] pipe error: {e}\n")
        return simple_frequency(sentences)

    word_freq: Counter = Counter()
    word_best: dict    = {}

    for doc, sent in zip(docs, sentences):
        gdex = compute_gdex(doc)
        for token in doc:
            if (token.pos_ not in TARGET_POS
                    or token.lemma_.lower() in STOPWORDS
                    or len(token.lemma_) < 3
                    or not token.is_alpha):
                continue
            lemma = token.lemma_.lower()
            word_freq[lemma] += 1
            if lemma not in word_best or gdex > word_best[lemma][1]:
                word_best[lemma] = (token.pos_, gdex, sent)

    results = [
        {
            "lemma":            lemma,
            "pos":              word_best[lemma][0],
            "frequency":        freq,
            "gdex_score":       word_best[lemma][1],
            "example_sentence": word_best[lemma][2],
        }
        for lemma, freq in word_freq.most_common()
        if lemma in word_best
    ]
    sys.stderr.write(f"[NLP] analyzed {len(sentences)} → {len(results)} words\n")
    return results


# ── 단순 빈도 폴백 ────────────────────────────────────────────────────────
def simple_frequency(sentences: list) -> list:
    stops = {
        "the","a","an","is","are","was","were","be","been","being","have","has",
        "had","do","does","did","will","would","could","should","may","might","can",
        "of","in","to","for","on","at","by","with","from","as","or","and","but",
        "not","this","that","it","i","you","he","she","we","they","what","which",
        "who","just","also","very","so","up","my","your","its","get","got","all",
        "one","two","more","than","then","into","about","over","after","before"
    }
    freq: Counter = Counter()
    examples: dict = {}
    for s in sentences:
        for w in re.split(r"[\s]+", s):
            clean = re.sub(r"[^a-zA-Z'-]", "", w).strip("-'").lower()
            if len(clean) < 3 or clean in stops:
                continue
            freq[clean] += 1
            if clean not in examples:
                examples[clean] = s

    return [
        {
            "lemma":            w,
            "pos":              "WORD",
            "frequency":        c,
            "gdex_score":       min(1.0, 0.3 + c * 0.05),
            "example_sentence": examples.get(w, ""),
        }
        for w, c in freq.most_common()
    ]


# ── 퀴즈 생성 ────────────────────────────────────────────────────────────
def generate_quiz(entries: list, count: int = 10, difficulty: float = 0.5) -> list:
    if len(entries) < 4:
        return []

    # 퀴즈 대상 단어 필터링
    valid_entries = [
        e for e in entries
        if is_valid_mask_word(
            e.get("lemma", ""),
            e.get("pos", "WORD"),
            e.get("example_sentence", "")
        )
    ]

    sys.stderr.write(f"[NLP] 전체 단어: {len(entries)}개 → 유효 단어(동사/형용사/부사): {len(valid_entries)}개\n")

    if len(valid_entries) < 4:
        valid_entries = entries
        sys.stderr.write("[NLP] 유효 단어 부족 → 전체 단어 사용\n")

    random.shuffle(valid_entries)
    sample  = valid_entries[:min(count, len(valid_entries))]
    quizzes = []

    for entry in sample:
        sentence = entry.get("example_sentence", "")
        correct  = entry["lemma"]

        distractors = get_distractors(sentence, correct, difficulty)

        if len(distractors) < 3:
            others      = [e["lemma"] for e in valid_entries if e["lemma"] != correct]
            distractors = random.sample(others, min(3, len(others)))

        choices = distractors + [correct]
        random.shuffle(choices)

        blanked = re.sub(
            re.escape(correct), "_____", sentence, count=1, flags=re.IGNORECASE
        ) if sentence else f"_____ ({correct})"

        quizzes.append({
            "word_id":    entry.get("word_id", 0),
            "question":   "다음 문장에서 빈칸에 알맞은 단어는?",
            "sentence":   blanked,
            "correct":    correct,
            "choices":    choices,
            "pos":        entry.get("pos", ""),
            "difficulty": difficulty,
        })

    sys.stderr.write(f"[NLP] quiz {len(quizzes)}개 생성 (difficulty={difficulty:.2f}, onnx={DISTILBERT_AVAILABLE})\n")
    return quizzes


# ── 진입점 ───────────────────────────────────────────────────────────────
if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")

    if len(sys.argv) < 2:
        print("[]")
        sys.exit(0)

    command   = sys.argv[1]
    raw_input = sys.stdin.read().strip()

    if not raw_input:
        sys.stderr.write("[NLP] Empty stdin\n")
        print("[]")
        sys.exit(0)

    try:
        data = json.loads(raw_input)
    except json.JSONDecodeError as e:
        sys.stderr.write(f"[NLP] JSON error: {e}\n")
        print("[]")
        sys.exit(1)

    if not isinstance(data, list):
        print("[]")
        sys.exit(0)

    if command == "analyze":
        print(json.dumps(analyze_sentences(data), ensure_ascii=False))

    elif command == "quiz":
        count      = int(sys.argv[2])   if len(sys.argv) > 2 else 10
        difficulty = float(sys.argv[3]) if len(sys.argv) > 3 else 0.5
        print(json.dumps(generate_quiz(data, count, difficulty), ensure_ascii=False))

    else:
        print("[]")
