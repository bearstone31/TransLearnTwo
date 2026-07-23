// ============================================================
// CaptureGalleryViewModel.cs [추가]
// 역할 : "캡처 관리" 갤러리 탭의 ViewModel.
//        캡처 이미지가 있는 번역 기록만 불러와 카드 형태로 보여준다.
//
// 필터 구조 (계단식) : 캡처 유형(OCR/Sound) → 날짜 → 응용프로그램
//   상위 필터를 바꾸면 하위 필터 옵션 목록이 그 조건에 맞게 다시 계산된다.
// ============================================================
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Linq;
using TransLearn.Models;

namespace TransLearn.ViewModels;

public partial class CaptureGalleryViewModel : ObservableObject
{
    private const string AllOption = "전체";

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private int _totalCount;

    /// <summary>로딩이 끝났는데 표시할 캡처가 하나도 없을 때만 true (빈 상태 안내용)</summary>
    public bool HasNoItems => !IsLoading && TotalCount == 0;

    [ObservableProperty] private string _selectedType = AllOption;
    [ObservableProperty] private string _selectedDate = AllOption;
    [ObservableProperty] private string _selectedApp = AllOption;

    public ObservableCollection<string> TypeOptions { get; } = new();
    public ObservableCollection<string> DateOptions { get; } = new();
    public ObservableCollection<string> AppOptions { get; } = new();

    public ObservableCollection<TranslationRecord> GalleryItems { get; } = new();

    private List<TranslationRecord> _allCaptures = new();

    public CaptureGalleryViewModel()
    {
        _ = LoadAsync();
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            _allCaptures = await App.Database.GetTranslationsWithImageAsync();
            RebuildTypeOptions();
            RebuildDateOptions();
            RebuildAppOptions();
            ApplyFilters();
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ── 계단식 필터 옵션 재계산 ──────────────────────────────────────────
    private IEnumerable<TranslationRecord> ByType() =>
        SelectedType == AllOption ? _allCaptures : _allCaptures.Where(r => r.CaptureTypeLabel == SelectedType);

    private IEnumerable<TranslationRecord> ByTypeAndDate() =>
        ByType().Where(r => SelectedDate == AllOption || r.DateLabel == SelectedDate);

    private static string AppLabel(TranslationRecord r) =>
        string.IsNullOrWhiteSpace(r.AppName) ? "(알 수 없음)" : r.AppName;

    private void RebuildTypeOptions()
    {
        var prev = SelectedType;
        TypeOptions.Clear();
        TypeOptions.Add(AllOption);
        foreach (var t in _allCaptures.Select(r => r.CaptureTypeLabel).Distinct().OrderBy(x => x))
            TypeOptions.Add(t);
        SelectedType = TypeOptions.Contains(prev) ? prev : AllOption;
    }

    private void RebuildDateOptions()
    {
        var prev = SelectedDate;
        DateOptions.Clear();
        DateOptions.Add(AllOption);
        foreach (var d in ByType().Select(r => r.DateLabel).Distinct().OrderByDescending(x => x))
            DateOptions.Add(d);
        SelectedDate = DateOptions.Contains(prev) ? prev : AllOption;
    }

    private void RebuildAppOptions()
    {
        var prev = SelectedApp;
        AppOptions.Clear();
        AppOptions.Add(AllOption);
        foreach (var a in ByTypeAndDate().Select(AppLabel).Distinct().OrderBy(x => x))
            AppOptions.Add(a);
        SelectedApp = AppOptions.Contains(prev) ? prev : AllOption;
    }

    private void ApplyFilters()
    {
        var q = ByTypeAndDate();
        if (SelectedApp != AllOption)
            q = q.Where(r => AppLabel(r) == SelectedApp);

        GalleryItems.Clear();
        foreach (var r in q.OrderByDescending(r => r.CapturedAt))
            GalleryItems.Add(r);
        TotalCount = GalleryItems.Count;
    }

    // ── 사용자가 콤보박스를 바꿀 때마다 하위 옵션 재계산 후 필터 적용 ──────
    partial void OnSelectedTypeChanged(string value)
    {
        RebuildDateOptions();
        RebuildAppOptions();
        ApplyFilters();
    }

    partial void OnSelectedDateChanged(string value)
    {
        RebuildAppOptions();
        ApplyFilters();
    }

    partial void OnSelectedAppChanged(string value)
    {
        ApplyFilters();
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(HasNoItems));
    partial void OnTotalCountChanged(int value) => OnPropertyChanged(nameof(HasNoItems));
}
