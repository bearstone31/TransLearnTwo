// ============================================================
// HistoryViewModel.cs
// 역할 : HistoryView의 MVVM ViewModel.
//        DB에서 번역 기록을 로드하고 날짜별 그룹핑, 검색/앱 필터를 제공.
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
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _filterType = "전체";
    [ObservableProperty] private int _totalCount;

    private ObservableCollection<TranslationRecord> _records = new();

    public CollectionViewSource GroupedView { get; } = new();

    // 기존: 전체, OCR, Sound
    // 변경: 전체, 앱 이름 목록
    public ObservableCollection<string> FilterOptions { get; } = new();

    public HistoryViewModel()
    {
        GroupedView.Source = _records;

        // 기존에는 CaptureTypeLabel로 먼저 그룹핑해서 OCR/Sound가 크게 나뉘었음.
        // 앱 필터 기능에 맞게 날짜 기준 그룹핑만 남김.
        GroupedView.GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(TranslationRecord.DateLabel)));

        GroupedView.Filter += OnFilter;

        _ = LoadAsync();
    }

    private void OnFilter(object sender, FilterEventArgs e)
    {
        if (e.Item is not TranslationRecord rec)
        {
            e.Accepted = false;
            return;
        }

        var matchSearch = string.IsNullOrWhiteSpace(SearchText)
            || rec.OriginalText.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || rec.Translated.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || rec.AppName.Contains(SearchText, StringComparison.OrdinalIgnoreCase);

        var matchApp = FilterType == "전체"
            || string.Equals(rec.AppName, FilterType, StringComparison.OrdinalIgnoreCase);

        e.Accepted = matchSearch && matchApp;
    }

    partial void OnSearchTextChanged(string value)
    {
        GroupedView.View?.Refresh();
        UpdateTotalCount();
    }

    partial void OnFilterTypeChanged(string value)
    {
        GroupedView.View?.Refresh();
        UpdateTotalCount();
    }

    private void UpdateTotalCount()
    {
        if (GroupedView.View == null)
        {
            TotalCount = _records.Count;
            return;
        }

        TotalCount = GroupedView.View.Cast<object>().Count();
    }

    [RelayCommand]
    private async Task DeleteRecordAsync(TranslationRecord record)
    {
        if (record == null) return;

        await App.Database.DeleteTranslationAsync(record.Id);

        _records.Remove(record);

        GroupedView.View?.Refresh();
        UpdateTotalCount();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;

        try
        {
            var selectedFilter = FilterType;

            var data = await App.Database.GetTranslationsAsync(limit: 500);
            var apps = await App.Database.GetTranslationSourceAppsAsync();

            _records.Clear();

            foreach (var item in data)
                _records.Add(item);

            FilterOptions.Clear();
            FilterOptions.Add("전체");

            foreach (var app in apps)
                FilterOptions.Add(app);

            if (!FilterOptions.Contains(selectedFilter))
                FilterType = "전체";
            else
                FilterType = selectedFilter;

            GroupedView.View?.Refresh();
            UpdateTotalCount();
        }
        finally
        {
            IsLoading = false;
        }
    }
}