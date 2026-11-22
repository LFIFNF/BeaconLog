using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BeaconLog;

public partial class JournalCategoriesWindow : Window
{
    private JournalCategory? renameTarget;
    private App BeaconLogApp => (App)Application.Current;

    public JournalCategoriesWindow()
    {
        InitializeComponent();
        RefreshList(JournalCategory.Ordinary);
    }

    private void RefreshList(JournalCategory? selected)
    {
        CategoryList.ItemsSource = BeaconLogApp.JournalCategories;
        CategoryList.SelectedItem = selected;
    }

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = CategoryList.SelectedIndex;
        RenameButton.IsEnabled = DeleteButton.IsEnabled = index > 0;
        UpButton.IsEnabled = index > 1;
        DownButton.IsEnabled = index > 0 && index < CategoryList.Items.Count - 1;
    }

    private void Add_Click(object sender, RoutedEventArgs e) => OpenEditor(null);

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (CategoryList.SelectedIndex > 0 && CategoryList.SelectedItem is JournalCategory category)
        {
            OpenEditor(category);
        }
    }

    private void OpenEditor(JournalCategory? category)
    {
        renameTarget = category;
        ActionError.Text = "";
        CategoryName.Text = category?.Name ?? "";
        CategoryEditor.Visibility = Visibility.Visible;
        CategoryName.Focus();
        CategoryName.SelectAll();
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveEditor();
    private void Cancel_Click(object sender, RoutedEventArgs e) => CategoryEditor.Visibility = Visibility.Collapsed;

    private void CategoryName_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SaveEditor();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CategoryEditor.Visibility = Visibility.Collapsed;
        }
    }

    private void SaveEditor()
    {
        var name = CategoryName.Text.Trim();
        if (!JournalCategory.IsValidName(name))
        {
            ActionError.Text = "分类名称不能为空，且不能含空白或斜杠。";
            return;
        }

        if (renameTarget is not { } target)
        {
            if (Apply(store => store.AddCategory(name)))
            {
                RefreshList(BeaconLogApp.JournalCategories[^1]);
            }
            return;
        }

        if (target.Name == name)
        {
            CategoryEditor.Visibility = Visibility.Collapsed;
            return;
        }

        var history = JournalCategoryHistoryPrompt.Ask(this, isDelete: false);
        if (history is null)
        {
            CategoryEditor.Visibility = Visibility.Collapsed;
            return;
        }

        if (Apply(store => store.RenameCategory(target.Id, name, history.Value)))
        {
            RefreshList(target with { Name = name });
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (CategoryList.SelectedIndex <= 0 || CategoryList.SelectedItem is not JournalCategory selected)
        {
            return;
        }

        var history = JournalCategoryHistoryPrompt.Ask(this, isDelete: true);
        if (history is { } apply && Apply(store => store.DeleteCategory(selected.Id, apply)))
        {
            RefreshList(JournalCategory.Ordinary);
        }
    }

    private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int offset)
    {
        if (CategoryList.SelectedIndex > 0 && CategoryList.SelectedItem is JournalCategory selected
            && Apply(store => store.MoveCategory(selected.Id, offset)))
        {
            RefreshList(selected);
        }
    }

    private bool Apply(System.Func<JournalStore, JournalData> change)
    {
        var error = BeaconLogApp.ChangeJournalCategories(change);
        ActionError.Text = error ?? "";
        if (error is not null)
        {
            return false;
        }

        CategoryEditor.Visibility = Visibility.Collapsed;
        return true;
    }
}
