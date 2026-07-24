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
# BASE_PATH : PyInstaller 가 내부 리소스를 풀어놓는 임시 폴더 (exe 안에 묶은 데이터용)
# EXE_DIR   : 실행 파일이 실제로 놓인 폴더 (밖에 둔 모델·토크나이저용)
#
# exe 로 묶이면 __file__ 은 임시 폴더를 가리키므로, 옆에 둔 큰 파일(345MB ONNX 등)을
# 찾으려면 sys.executable 기준으로 봐야 한다.
# .py 로 그냥 실행할 때는 둘 다 스크립트 폴더가 되어 기존 동작과 동일하다.
def _get_base_path():
    if getattr(sys, 'frozen', False):
        return sys._MEIPASS
    return os.path.dirname(os.path.abspath(__file__))


def _get_exe_dir():
    if getattr(sys, 'frozen', False):
        return os.path.dirname(os.path.abspath(sys.executable))
    return os.path.dirname(os.path.abspath(__file__))


BASE_PATH = _get_base_path()
EXE_DIR   = _get_exe_dir()

# 리소스를 찾을 폴더 목록 (앞에서부터 순서대로 탐색)
#   EXE_DIR    : onedir 배포 시 exe 와 같은 폴더
#   EXE_DIR/.. : Python/nlp_analyzer/ 안에 exe, Python/ 아래에 models 가 있는 배치
#   BASE_PATH  : exe 안에 함께 묶은 경우
SEARCH_DIRS = [
    EXE_DIR,
    os.path.abspath(os.path.join(EXE_DIR, "..")),
    BASE_PATH,
]


def find_resource(*relative_parts):
    """SEARCH_DIRS 를 순서대로 뒤져 실제로 존재하는 첫 경로를 반환한다. 없으면 None."""
    for d in SEARCH_DIRS:
        candidate = os.path.join(d, *relative_parts)
        if os.path.exists(candidate):
            return candidate
    return None


def _resolve_spacy_model():
    """
    spaCy 모델 폴더를 찾는다.
    pip 로 설치된 en_core_web_sm 은 '껍데기 패키지'이고 실제 모델은
    그 안의 버전 폴더(en_core_web_sm-3.8.0 등)에 들어 있다.
    config.cfg 가 있는 쪽을 실제 모델로 판단한다.
    """
    base = find_resource("en_core_web_sm")
    if not base:
        return None

    if os.path.exists(os.path.join(base, "config.cfg")):
        return base

    try:
        for name in sorted(os.listdir(base)):
            sub = os.path.join(base, name)
            if os.path.isdir(sub) and os.path.exists(os.path.join(sub, "config.cfg")):
                return sub
    except Exception:
        pass

    return None


# NLTK 데이터는 사용자 홈 폴더에 다운로드되므로 새 PC 에는 없다.
# 배포본에 nltk_data 폴더를 함께 넣어두면 여기서 자동으로 잡아준다. (없어도 동작에 지장 없음)
_nltk_dir = find_resource("nltk_data")
if _nltk_dir:
    os.environ["NLTK_DATA"] = _nltk_dir
    sys.stderr.write(f"[NLP] nltk_data: {_nltk_dir}\n")

sys.stderr.write(f"[NLP] EXE_DIR={EXE_DIR}\n")
sys.stderr.write(f"[NLP] BASE_PATH={BASE_PATH}\n")

# ── spaCy 초기화 ─────────────────────────────────────────────────────────
SPACY_AVAILABLE = False
nlp = None

try:
    import spacy
    model_path = _resolve_spacy_model()
    if model_path:
        sys.stderr.write(f"[NLP] spaCy 모델: {model_path}\n")
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


# ── 빈칸 처리 헬퍼 ───────────────────────────────────────────────────────
def _replace_word_form(sentence: str, base_word: str, replacement: str) -> str:
    """
    문장에서 base_word 로 시작하는 단어 하나를 replacement 로 바꾼다.

    spaCy 를 쓰면 lemma 가 원형(jump)으로 오는데 문장에는 활용형(jumps)이
    들어 있다. 단순 치환하면 'The fox _____s over' 처럼 어미가 남으므로,
    \\w* 를 붙여 뒤따르는 글자까지 함께 지운다.
    실패하면 원래 방식으로 한 번 더 시도한다.
    """
    if not sentence:
        return sentence

    result = re.sub(
        r"\b" + re.escape(base_word) + r"\w*", replacement, sentence,
        count=1, flags=re.IGNORECASE
    )
    if replacement in result:
        return result

    return re.sub(
        re.escape(base_word), replacement, sentence,
        count=1, flags=re.IGNORECASE
    )


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

    # SEARCH_DIRS 기준으로 탐색한다 (exe 로 묶여도 정확히 찾는다)
    onnx_path = find_resource("models", "my_distilbert.onnx")

    if onnx_path:
        ort_session = ort.InferenceSession(onnx_path)

        tokenizer_path = find_resource("tokenizer")
        if not tokenizer_path:
            # 마지막 수단 — 인터넷에서 받아온다 (오프라인이면 실패)
            tokenizer_path = "distilbert-base-uncased"
            sys.stderr.write("[NLP] 로컬 tokenizer 없음 → 온라인 모델명 사용\n")

        hf_tokenizer = DistilBertTokenizer.from_pretrained(tokenizer_path)
        DISTILBERT_AVAILABLE = True
        sys.stderr.write(f"[NLP] myDistilBERT 로드 성공: {onnx_path}\n")
    else:
        sys.stderr.write(f"[NLP] ONNX 파일 없음 (탐색 폴더: {SEARCH_DIRS})\n")
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

        # 원형(jump)과 문장 속 활용형(jumps)이 다를 수 있으므로 어미까지 함께 마스킹한다.
        # 그러지 않으면 "[MASK]s over ..." 같은 입력이 들어가 오답 품질이 크게 떨어진다.
        masked = _replace_word_form(sentence, correct, "[MASK]")
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

    # 유효 단어가 count보다 부족하면 나머지로 보충
    if len(valid_entries) < count:
        extras = [e for e in entries if e not in valid_entries]
        random.shuffle(extras)
        valid_entries = valid_entries + extras
        valid_entries = valid_entries[:count]
        sys.stderr.write(f"[NLP] 유효 단어 부족 → 보충 후 {len(valid_entries)}개\n")

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

        # 활용형 어미까지 함께 지운다 (jump → "The fox _____ over", not "_____s over")
        blanked = _replace_word_form(sentence, correct, "_____") if sentence \
            else f"_____ ({correct})"

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
        print("[]")https://client-api.arkoselabs.com/fc/assets/ec-game-core/game-core/1.37.0/standard/index.html?session=60118c515e85003c7.8172532104&r=ap-southeast-1&meta=7&meta_height=325&metabgclr=%23ffffff&metaiconclr=%23757575&mainbgclr=%23ffffff&maintxtclr=%231B1B1B&guitextcolor=%23747474&lang=ko&pk=B7D8911C-5CC8-A9A3-35B0-554ACEE604DA&at=40&ag=101&cdn_url=https%3A%2F%2Fclient-api.arkoselabs.com%2Fcdn%2Ffc&surl=https%3A%2F%2Fclient-api.arkoselabs.com&smurl=https%3A%2F%2Fclient-api.arkoselabs.com%2Fcdn%2Ffc%2Fassets%2Fstyle-manager#
