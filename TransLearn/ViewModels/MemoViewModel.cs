using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using TransLearn.Models;

namespace TransLearn.ViewModels;

public partial class MemoViewModel : ObservableObject
{
    public ObservableCollection<MemoItem> Memos { get; } = new();

    [ObservableProperty] private MemoItem? _selectedMemo;
    [ObservableProperty] private string _memoContent = "";
    [ObservableProperty] private string _memoDescription = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _statusText = "메모장을 사용할 준비가 되었습니다.";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _sortAscending = false;

    public int TotalCount => Memos.Count;

    public string DateSortArrow => SortAscending ? "▲" : "▼";

    partial void OnSelectedMemoChanged(MemoItem? value)
    {
        if (value == null) return;

        MemoContent = value.Content;
        MemoDescription = value.Description;
    }

    partial void OnSearchTextChanged(string value)
    {
        _ = LoadMemosAsync();
    }

    [RelayCommand]
    private async Task LoadMemosAsync()
    {
        IsLoading = true;

        try
        {
            var data = await App.Database.GetMemosAsync(SearchText, SortAscending);

            Memos.Clear();

            foreach (var memo in data)
                Memos.Add(memo);

            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(DateSortArrow));

            StatusText = $"메모 {Memos.Count}개 로드됨";
        }
        catch (Exception ex)
        {
            StatusText = $"메모 로드 오류: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SaveMemoAsync()
    {
        if (string.IsNullOrWhiteSpace(MemoContent))
        {
            StatusText = "메모할 영어 단어 또는 문장을 입력하세요.";
            return;
        }

        try
        {
            if (SelectedMemo == null)
            {
                await App.Database.InsertMemoAsync(
                    MemoContent.Trim(),
                    MemoDescription.Trim());

                StatusText = "새 메모가 저장되었습니다.";
            }
            else
            {
                await App.Database.UpdateMemoAsync(
                    SelectedMemo.Id,
                    MemoContent.Trim(),
                    MemoDescription.Trim());

                StatusText = "메모가 수정되었습니다.";
            }

            await LoadMemosAsync();
            ClearInput();
        }
        catch (Exception ex)
        {
            StatusText = $"메모 저장 오류: {ex.Message}";
        }
    }

    [RelayCommand]
    private void NewMemo()
    {
        ClearInput();
        StatusText = "새 메모를 작성할 수 있습니다.";
    }

    [RelayCommand]
    private async Task DeleteMemoAsync(MemoItem? memo)
    {
        if (memo == null) return;

        try
        {
            await App.Database.DeleteMemoAsync(memo.Id);

            if (SelectedMemo?.Id == memo.Id)
                ClearInput();

            await LoadMemosAsync();

            StatusText = "메모가 삭제되었습니다.";
        }
        catch (Exception ex)
        {
            StatusText = $"메모 삭제 오류: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SortByDateAsync()
    {
        SortAscending = !SortAscending;
        OnPropertyChanged(nameof(DateSortArrow));

        await LoadMemosAsync();
    }

    private void ClearInput()
    {
        SelectedMemo = null;
        MemoContent = "";
        MemoDescription = "";
    }
}