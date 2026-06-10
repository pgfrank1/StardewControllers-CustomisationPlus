using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using PropertyChanged.SourceGenerator;

namespace StarControl.UI;

internal partial class PagerViewModel<T> : INotifyPropertyChanged, INotifyPropertyChanging
    where T : PageViewModel
{
    public T? SelectedPage =>
        SelectedPageIndex >= 0 && SelectedPageIndex < Pages.Count ? Pages[SelectedPageIndex] : null;

    [Notify]
    private Vector2 contentPanelSize;

    [Notify]
    private ObservableCollection<T> pages = [];

    [Notify]
    private int selectedPageIndex = -1;

    public PagerViewModel()
    {
        Pages.CollectionChanged += Pages_CollectionChanged;
    }

    public bool HandleButtonPress(SButton button)
    {
        if (Pages.Count == 0)
        {
            return false;
        }
        switch (button)
        {
            case SButton.LeftTrigger:
                SelectPage((SelectedPageIndex + Pages.Count - 1) % Pages.Count);
                return true;
            case SButton.RightTrigger:
                SelectPage((SelectedPageIndex + 1) % Pages.Count);
                return true;
            default:
                return false;
        }
    }

    public bool SelectPage(int index)
    {
        if (index < 0 || index == SelectedPageIndex)
        {
            return false;
        }
        Game1.playSound("smallSelect");
        SelectedPageIndex = index;
        return true;
    }

    private void OnContentPanelSizeChanged()
    {
        UpdatePageTransforms();
    }

    private void OnPagesChanged(
        ObservableCollection<T>? oldValue,
        ObservableCollection<T>? newValue
    )
    {
        if (oldValue is not null)
        {
            oldValue.CollectionChanged -= Pages_CollectionChanged;
        }
        SelectedPageIndex = Pages.Count > 0 ? 0 : -1;
        if (newValue is not null)
        {
            newValue.CollectionChanged += Pages_CollectionChanged;
        }
    }

    private void OnSelectedPageIndexChanged(int oldValue, int newValue)
    {
        if (oldValue >= 0 && oldValue < Pages.Count)
        {
            Pages[oldValue].Selected = false;
        }
        if (newValue >= 0 && newValue < Pages.Count)
        {
            Pages[newValue].Selected = true;
        }
        UpdatePageTransforms();
    }

    private void Pages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (SelectedPageIndex >= Pages.Count)
        {
            SelectedPageIndex = Pages.Count - 1;
        }
        else if (SelectedPageIndex < 0 && Pages.Count > 0)
        {
            SelectedPageIndex = 0;
        }
    }

    private void UpdatePageTransforms()
    {
        for (int i = 0; i < Pages.Count; i++)
        {
            var page = Pages[i];
            if (ContentPanelSize != Vector2.Zero)
            {
                page.Transform = new(new(ContentPanelSize.X * (i - SelectedPageIndex), 0));
                // Setting visible only when the page - or a subsequent page - is actually selected
                // prevents awkward transition animations from playing when the menu is first shown.
                if (i <= SelectedPageIndex)
                {
                    page.Visible = true;
                }
            }
        }
    }
}

internal partial class PageViewModel(int index)
{
    public int DisplayIndex => Index + 1; // 1-based index if displayed in a numeric page list
    public int Index => index;

    [Notify]
    private bool selected;

    [Notify]
    private Transform transform = new(Vector2.Zero);

    [Notify]
    private bool visible;
}

internal record Transform(Vector2 Translation, Vector2 Scale)
{
    public static readonly Transform None = new(Vector2.Zero, Vector2.One);

    public Transform(Vector2 translation)
        : this(translation, Vector2.One) { }
}
