using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace EditBuddy
{
    // Shared by the review companion, export preview and MSI portal. No external assets.
    internal static class MatrixTheme
    {
        internal static readonly Brush Background = Brush("#0a0904");
        internal static readonly Brush Panel = Brush("#121107");
        internal static readonly Brush Raised = Brush("#1b190c");
        internal static readonly Brush Line = Brush("#35301a");
        internal static readonly Brush Ink = Brush("#eee9cf");
        internal static readonly Brush Muted = Brush("#aaa17a");
        internal static readonly Brush Accent = Brush("#ffe66b");
        internal static readonly Brush Error = Brush("#f08d83");
        internal static readonly FontFamily Mono = new FontFamily("Cascadia Code, Consolas, Courier New");

        private static Brush Brush(string value)
        {
            SolidColorBrush brush = (SolidColorBrush)new BrushConverter().ConvertFromString(value);
            brush.Freeze();
            return brush;
        }

        internal static void Apply(Window window)
        {
            window.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(Styles));
            window.Background = Background;
            window.Foreground = Ink;
            window.FontFamily = Mono;
            window.FontSize = 13;
            TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
            window.UseLayoutRounding = true;
        }

        internal static void Primary(Button button)
        {
            button.Background = Accent;
            button.Foreground = Background;
            button.BorderBrush = Accent;
            button.FontWeight = FontWeights.SemiBold;
        }

        internal static TextBlock Eyebrow(string text)
        {
            return new TextBlock { Text = text, Foreground = Muted, FontSize = 10,
                Margin = new Thickness(0, 0, 0, 9), FontFamily = Mono };
        }

        internal static Border Header(UIElement child)
        {
            Grid grid = new Grid { ClipToBounds = true };
            // Static code texture keeps the Matrix atmosphere quiet while editing.
            DrawingGroup scanlines = new DrawingGroup();
            scanlines.Children.Add(new GeometryDrawing(Panel, null, new RectangleGeometry(new Rect(0, 0, 8, 4))));
            scanlines.Children.Add(new GeometryDrawing(Brush("#101006"), null, new RectangleGeometry(new Rect(0, 0, 8, 1))));
            DrawingBrush texture = new DrawingBrush(scanlines) { TileMode = TileMode.Tile,
                ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 8, 4), Stretch = Stretch.None };
            texture.Freeze(); grid.Background = texture;
            FormattedText binary = new FormattedText("01001  10110  01101\n10110  00101  11010\n00101  11010  01001\n11010  01001  10110",
                System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 11, Accent, 1.0);
            binary.LineHeight = 18;
            DrawingImage codeImage = new DrawingImage(new GeometryDrawing(Accent, null, binary.BuildGeometry(new Point(0, 0))));
            codeImage.Freeze();
            Image code = new Image { Source = codeImage, Stretch = Stretch.None, Opacity = 0.10,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 12, 16, 0), IsHitTestVisible = false, Focusable = false };
            grid.Children.Add(code); grid.Children.Add(child);
            return new Border { Child = grid, BorderBrush = Line, BorderThickness = new Thickness(0, 2, 0, 1) };
        }

        private const string Styles = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <SolidColorBrush x:Key='BuddyPanel' Color='#121107'/>
  <SolidColorBrush x:Key='BuddyRaised' Color='#1b190c'/>
  <SolidColorBrush x:Key='BuddyLine' Color='#35301a'/>
  <SolidColorBrush x:Key='BuddyOutline' Color='#756b40'/>
  <SolidColorBrush x:Key='BuddyInk' Color='#eee9cf'/>
  <SolidColorBrush x:Key='BuddyMuted' Color='#aaa17a'/>
  <SolidColorBrush x:Key='BuddyAccent' Color='#ffe66b'/>
  <SolidColorBrush x:Key='BuddySelected' Color='#373016'/>
  <Style x:Key='BuddyFocus' TargetType='{x:Type Control}'>
    <Setter Property='Template'><Setter.Value><ControlTemplate>
      <Rectangle Margin='2' Stroke='#ffe66b' StrokeThickness='1' StrokeDashArray='2 2' IsHitTestVisible='False'/>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType='{x:Type Button}'>
    <Setter Property='Background' Value='{StaticResource BuddyRaised}'/>
    <Setter Property='Foreground' Value='{StaticResource BuddyInk}'/>
    <Setter Property='BorderBrush' Value='{StaticResource BuddyOutline}'/>
    <Setter Property='BorderThickness' Value='1'/><Setter Property='Padding' Value='12,8'/>
    <Setter Property='Margin' Value='0,0,7,7'/><Setter Property='FontSize' Value='12'/>
    <Setter Property='MinHeight' Value='34'/><Setter Property='FocusVisualStyle' Value='{StaticResource BuddyFocus}'/>
    <Setter Property='HorizontalContentAlignment' Value='Center'/><Setter Property='VerticalContentAlignment' Value='Center'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type Button}'>
      <Border x:Name='Surface' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='5' Padding='{TemplateBinding Padding}'>
        <ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='{TemplateBinding VerticalContentAlignment}' RecognizesAccessKey='True'/>
      </Border>
      <ControlTemplate.Triggers>
        <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Surface' Property='BorderBrush' Value='{StaticResource BuddyAccent}'/></Trigger>
        <Trigger Property='IsKeyboardFocusWithin' Value='True'><Setter TargetName='Surface' Property='BorderBrush' Value='{StaticResource BuddyAccent}'/></Trigger>
        <Trigger Property='IsPressed' Value='True'><Setter TargetName='Surface' Property='Opacity' Value='0.7'/></Trigger>
        <Trigger Property='IsEnabled' Value='False'><Setter TargetName='Surface' Property='Opacity' Value='0.5'/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType='{x:Type TextBox}'>
    <Setter Property='Background' Value='{StaticResource BuddyPanel}'/><Setter Property='Foreground' Value='{StaticResource BuddyInk}'/>
    <Setter Property='CaretBrush' Value='{StaticResource BuddyAccent}'/>
    <Setter Property='SelectionBrush' Value='{StaticResource BuddyAccent}'/><Setter Property='SelectionTextBrush' Value='#0a0904'/><Setter Property='SelectionOpacity' Value='1'/>
    <Setter Property='BorderBrush' Value='{StaticResource BuddyOutline}'/><Setter Property='BorderThickness' Value='1'/>
    <Setter Property='Padding' Value='9,8'/><Setter Property='Margin' Value='0,3,0,10'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type TextBox}'>
      <Border x:Name='Surface' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='4'>
        <ScrollViewer x:Name='PART_ContentHost' Margin='{TemplateBinding Padding}'/>
      </Border>
      <ControlTemplate.Triggers>
        <Trigger Property='IsKeyboardFocusWithin' Value='True'><Setter TargetName='Surface' Property='BorderBrush' Value='{StaticResource BuddyAccent}'/></Trigger>
        <Trigger Property='IsEnabled' Value='False'><Setter Property='Foreground' Value='{StaticResource BuddyMuted}'/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType='{x:Type CheckBox}'>
    <Setter Property='Foreground' Value='{StaticResource BuddyInk}'/><Setter Property='FocusVisualStyle' Value='{StaticResource BuddyFocus}'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type CheckBox}'>
      <Grid Background='Transparent'><Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>
        <Border x:Name='Box' Width='15' Height='15' Margin='0,2,9,0' VerticalAlignment='Top' Background='{StaticResource BuddyPanel}' BorderBrush='{StaticResource BuddyOutline}' BorderThickness='1' CornerRadius='2'>
          <Path x:Name='Check' Visibility='Collapsed' Data='M 2,6 L 5,9 L 11,2' Stroke='{StaticResource BuddyAccent}' StrokeThickness='2' VerticalAlignment='Center' HorizontalAlignment='Center'/>
        </Border>
        <ContentPresenter Grid.Column='1' RecognizesAccessKey='True' VerticalAlignment='Center'/>
      </Grid>
      <ControlTemplate.Triggers>
        <Trigger Property='IsChecked' Value='True'><Setter TargetName='Check' Property='Visibility' Value='Visible'/></Trigger>
        <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Box' Property='BorderBrush' Value='{StaticResource BuddyAccent}'/></Trigger>
        <Trigger Property='IsKeyboardFocusWithin' Value='True'><Setter TargetName='Box' Property='BorderBrush' Value='{StaticResource BuddyAccent}'/></Trigger>
        <Trigger Property='IsEnabled' Value='False'><Setter Property='Foreground' Value='{StaticResource BuddyMuted}'/><Setter TargetName='Box' Property='Opacity' Value='0.55'/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType='{x:Type TabItem}'>
    <Setter Property='Foreground' Value='{StaticResource BuddyMuted}'/><Setter Property='Background' Value='Transparent'/>
    <Setter Property='FocusVisualStyle' Value='{StaticResource BuddyFocus}'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type TabItem}'>
      <Border x:Name='Tab' Background='{TemplateBinding Background}' BorderBrush='Transparent' BorderThickness='0,0,0,2' Padding='{TemplateBinding Padding}'>
        <ContentPresenter ContentSource='Header' RecognizesAccessKey='True' HorizontalAlignment='Center'/>
      </Border>
      <ControlTemplate.Triggers>
        <Trigger Property='IsSelected' Value='True'><Setter Property='Foreground' Value='{StaticResource BuddyAccent}'/><Setter TargetName='Tab' Property='BorderBrush' Value='{StaticResource BuddyAccent}'/><Setter Property='Background' Value='{StaticResource BuddyRaised}'/></Trigger>
        <Trigger Property='IsMouseOver' Value='True'><Setter Property='Foreground' Value='{StaticResource BuddyInk}'/></Trigger>
        <Trigger Property='IsKeyboardFocusWithin' Value='True'><Setter TargetName='Tab' Property='BorderBrush' Value='{StaticResource BuddyAccent}'/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType='{x:Type TabControl}'>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type TabControl}'>
      <DockPanel KeyboardNavigation.TabNavigation='Local'>
        <Border DockPanel.Dock='Top' BorderBrush='{StaticResource BuddyLine}' BorderThickness='0,0,0,1'><TabPanel IsItemsHost='True' KeyboardNavigation.TabNavigation='Once'/></Border>
        <ContentPresenter x:Name='PART_SelectedContentHost' ContentSource='SelectedContent' Margin='0'/>
      </DockPanel>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType='{x:Type ListBox}'>
    <Setter Property='Background' Value='{StaticResource BuddyPanel}'/><Setter Property='Foreground' Value='{StaticResource BuddyInk}'/>
    <Setter Property='BorderBrush' Value='{StaticResource BuddyOutline}'/><Setter Property='BorderThickness' Value='1'/>
    <Setter Property='ScrollViewer.HorizontalScrollBarVisibility' Value='Auto'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ListBox}'>
      <Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='4' Padding='3'>
        <ScrollViewer Focusable='False' Padding='{TemplateBinding Padding}'><ItemsPresenter/></ScrollViewer>
      </Border>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style x:Key='BuddyItem' TargetType='{x:Type ListBoxItem}'>
    <Setter Property='Foreground' Value='{StaticResource BuddyInk}'/><Setter Property='Padding' Value='7,8'/>
    <Setter Property='HorizontalContentAlignment' Value='Stretch'/><Setter Property='FocusVisualStyle' Value='{StaticResource BuddyFocus}'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ListBoxItem}'>
      <Border x:Name='Item' Padding='{TemplateBinding Padding}' Background='Transparent' BorderBrush='Transparent' BorderThickness='2,0,0,0' CornerRadius='2'>
        <ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}'/>
      </Border>
      <ControlTemplate.Triggers>
        <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Item' Property='Background' Value='{StaticResource BuddyRaised}'/></Trigger>
        <Trigger Property='IsSelected' Value='True'><Setter TargetName='Item' Property='Background' Value='{StaticResource BuddySelected}'/><Setter TargetName='Item' Property='BorderBrush' Value='{StaticResource BuddyAccent}'/><Setter Property='Foreground' Value='{StaticResource BuddyAccent}'/></Trigger>
        <Trigger Property='IsKeyboardFocusWithin' Value='True'><Setter TargetName='Item' Property='BorderBrush' Value='{StaticResource BuddyAccent}'/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType='{x:Type ListBoxItem}' BasedOn='{StaticResource BuddyItem}'/>
  <Style TargetType='{x:Type ComboBoxItem}'>
    <Setter Property='Foreground' Value='{StaticResource BuddyInk}'/><Setter Property='Padding' Value='9,8'/><Setter Property='Background' Value='{StaticResource BuddyPanel}'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ComboBoxItem}'>
      <Border x:Name='Item' Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}' BorderBrush='Transparent' BorderThickness='2,0,0,0'><ContentPresenter/></Border>
      <ControlTemplate.Triggers>
        <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Item' Property='Background' Value='{StaticResource BuddySelected}'/><Setter TargetName='Item' Property='BorderBrush' Value='{StaticResource BuddyAccent}'/></Trigger>
        <Trigger Property='IsSelected' Value='True'><Setter Property='Foreground' Value='{StaticResource BuddyAccent}'/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType='{x:Type ComboBox}'>
    <Setter Property='Foreground' Value='{StaticResource BuddyInk}'/><Setter Property='Background' Value='{StaticResource BuddyRaised}'/>
    <Setter Property='BorderBrush' Value='{StaticResource BuddyOutline}'/><Setter Property='Padding' Value='9,8'/>
    <Setter Property='FocusVisualStyle' Value='{StaticResource BuddyFocus}'/>
    <Setter Property='ScrollViewer.HorizontalScrollBarVisibility' Value='Auto'/><Setter Property='ScrollViewer.VerticalScrollBarVisibility' Value='Auto'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ComboBox}'>
      <Grid>
        <ToggleButton x:Name='Toggle' Focusable='False' IsChecked='{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}' ClickMode='Press'>
          <ToggleButton.Template><ControlTemplate TargetType='{x:Type ToggleButton}'>
            <Border x:Name='Surface' Background='{Binding Background, RelativeSource={RelativeSource AncestorType={x:Type ComboBox}}}' BorderBrush='{Binding BorderBrush, RelativeSource={RelativeSource AncestorType={x:Type ComboBox}}}' BorderThickness='1' CornerRadius='4'>
              <Path Data='M 0,0 L 4,4 L 8,0' Stroke='{StaticResource BuddyAccent}' StrokeThickness='1.5' HorizontalAlignment='Right' VerticalAlignment='Center' Margin='10'/>
            </Border>
            <ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Surface' Property='BorderBrush' Value='{StaticResource BuddyAccent}'/></Trigger></ControlTemplate.Triggers>
          </ControlTemplate></ToggleButton.Template>
        </ToggleButton>
        <ContentPresenter IsHitTestVisible='False' Margin='10,9,30,9' VerticalAlignment='Center' Content='{TemplateBinding SelectionBoxItem}' ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}' ContentTemplateSelector='{TemplateBinding ItemTemplateSelector}'/>
        <Popup x:Name='PART_Popup' Placement='Bottom' IsOpen='{TemplateBinding IsDropDownOpen}' AllowsTransparency='True' Focusable='False'>
          <Border Background='{StaticResource BuddyPanel}' BorderBrush='{StaticResource BuddyOutline}' BorderThickness='1' CornerRadius='4' MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}' MaxHeight='{TemplateBinding MaxDropDownHeight}' Padding='3'>
            <ScrollViewer CanContentScroll='True'><ItemsPresenter KeyboardNavigation.DirectionalNavigation='Contained'/></ScrollViewer>
          </Border>
        </Popup>
      </Grid>
      <ControlTemplate.Triggers>
        <Trigger Property='IsKeyboardFocusWithin' Value='True'><Setter Property='BorderBrush' Value='{StaticResource BuddyAccent}'/></Trigger>
        <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.5'/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType='{x:Type ToolTip}'>
    <Setter Property='Background' Value='{StaticResource BuddyRaised}'/><Setter Property='Foreground' Value='{StaticResource BuddyInk}'/><Setter Property='BorderBrush' Value='{StaticResource BuddyOutline}'/>
  </Style>
  <Style TargetType='{x:Type ScrollBar}'>
    <Setter Property='Background' Value='{StaticResource BuddyPanel}'/><Setter Property='Width' Value='11'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ScrollBar}'>
      <Grid Background='{TemplateBinding Background}'>
        <Track x:Name='PART_Track' IsDirectionReversed='True' Orientation='{TemplateBinding Orientation}'>
          <Track.DecreaseRepeatButton><RepeatButton x:Name='PageBefore' Command='ScrollBar.PageUpCommand' Focusable='False'><RepeatButton.Template><ControlTemplate><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
          <Track.Thumb><Thumb><Thumb.Template><ControlTemplate><Border x:Name='Thumb' Background='#756b40' CornerRadius='3' Margin='2'/><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Thumb' Property='Background' Value='#ffe66b'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
          <Track.IncreaseRepeatButton><RepeatButton x:Name='PageAfter' Command='ScrollBar.PageDownCommand' Focusable='False'><RepeatButton.Template><ControlTemplate><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>
        </Track>
      </Grid>
      <ControlTemplate.Triggers><Trigger Property='Orientation' Value='Horizontal'>
        <Setter Property='Width' Value='Auto'/><Setter Property='Height' Value='11'/><Setter TargetName='PART_Track' Property='IsDirectionReversed' Value='False'/>
        <Setter TargetName='PageBefore' Property='Command' Value='ScrollBar.PageLeftCommand'/>
        <Setter TargetName='PageAfter' Property='Command' Value='ScrollBar.PageRightCommand'/>
      </Trigger></ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
</ResourceDictionary>";
    }
}
