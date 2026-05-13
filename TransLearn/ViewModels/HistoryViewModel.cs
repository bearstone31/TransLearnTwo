// ============================================================
// HistoryViewModel.cs
// 역할 : HistoryView의 MVVM ViewModel.
//        DB에서 번역 기록을 로드하고 날짜별 그룹핑, 검색 필터를 제공.
// ============================================================
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Windows.Data;
using System.Threading.Tasks;
using TransLearn.Models;

namespace TransLearn.ViewModels;

public partial class HistoryViewModel : ObservableObject
{
    [ObservableProperty] private bool   _isLoading;
    [ObservableProperty] private string _searchText   = "";
    [ObservableProperty] private string _filterType   = "전체";
    [ObservableProperty] private int    _totalCount;

    private ObservableCollection<TranslationRecord> _records = new();
    public CollectionViewSource GroupedView { get; } = new();

    public List<string> FilterOptions { get; } = new() { "전체", "OCR", "Sound" };

    public HistoryViewModel()
    {
        GroupedView.Source = _records;
        GroupedView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TranslationRecord.CaptureTypeLabel)));
        GroupedView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TranslationRecord.DateLabel)));
        GroupedView.Filter += OnFilter;
    }

    private void OnFilter(object sender, FilterEventArgs e)
    {
        if (e.Item is not TranslationRecord rec) { e.Accepted = false; return; }
        var matchSearch = string.IsNullOrWhiteSpace(SearchText)
            || rec.OriginalText.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || rec.Translated.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
        var matchType = FilterType == "전체"
            || (FilterType == "OCR"   && rec.CaptureType == CaptureType.OCR)
            || (FilterType == "Sound" && rec.CaptureType == CaptureType.Sound);
        e.Accepted = matchSearch && matchType;
    }

    partial void OnSearchTextChanged(string value)  => GroupedView.View?.Refresh();
    partial void OnFilterTypeChanged(string value)  => GroupedView.View?.Refresh();


    [RelayCommand]
    private async Task DeleteRecordAsync(TranslationRecord record)
    {
        if (record == null) return;

        // 1. 실제 로컬 SQLite DB에서 삭제
        await App.Database.DeleteTranslationAsync(record.Id);

        // 2. 화면에 바인딩된 원본 리스트에서 해당 항목 제거
        // (이 리스트 이름이 Records, AllRecords, Items 등 기존 코드에 맞게 이름을 맞춰주세요)
        // 예: Records.Remove(record);

        // 3. (필요 시) 그룹화된 뷰 갱신
        await LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var data = await Task.Run(() =>
                App.Database.GetTranslationsAsync().GetAwaiter().GetResult());

            _records = new ObservableCollection<TranslationRecord>(data);
            GroupedView.Source = _records;
            GroupedView.Filter += OnFilter;
            TotalCount = _records.Count;
        }
        finally { IsLoading = false; }
    }
}
