namespace RemoteDesktop.Maui;

/// <summary>App license (GPLv2) and third-party notices for the bundled campus-VPN engine.</summary>
public sealed class LicensePage : ContentPage
{
    private static readonly (string Title, string Body)[] Sections =
    {
        ("LiteRemote untuk Android",
         "Aplikasi ini (src/RemoteDesktop.Maui) dirilis di bawah GNU General Public License versi 2 (GPLv2). " +
         "Anda bebas menjalankan, mempelajari, membagikan, dan mengubahnya sesuai ketentuan GPLv2. " +
         "Bagian lain proyek LiteRemote (host Windows, viewer Windows, relay, pustaka Shared) berlisensi MIT.\n" +
         "Kode sumber lengkap: https://github.com/amirullah/lite-remote-desktop\n" +
         "Teks lisensi: https://www.gnu.org/licenses/old-licenses/gpl-2.0.html"),
        ("OpenVPN 2.6 (mesin VPN kampus)",
         "Copyright (C) 2002-2025 OpenVPN Inc. dan kontributor. Dilisensikan di bawah GNU GPL versi 2, " +
         "dengan pengecualian khusus yang mengizinkan linking dengan OpenSSL. Dibangun dari fork ics-openvpn " +
         "(Arne Schwabe, GPLv2). Skrip build dan sumber persis yang dipakai tersedia di repositori LiteRemote.\n" +
         "https://openvpn.net · https://github.com/schwabe/ics-openvpn\n" +
         "LiteRemote tidak berafiliasi dengan OpenVPN Inc.; \"OpenVPN\" adalah merek dagang OpenVPN Inc."),
        ("OpenSSL",
         "Copyright (c) 1998-2025 The OpenSSL Project Authors. OpenSSL 3.x dilisensikan di bawah Apache License 2.0.\n" +
         "https://www.openssl.org/source/license.html"),
        ("Mbed TLS (bila dipakai pada build)",
         "Copyright The Mbed TLS Contributors. Dilisensikan di bawah Apache License 2.0 atau GPL-2.0-or-later.\n" +
         "https://github.com/Mbed-TLS/mbedtls"),
        ("LZ4",
         "Copyright (c) 2011-2020 Yann Collet. Pustaka LZ4 dilisensikan di bawah BSD 2-Clause License.\n" +
         "https://github.com/lz4/lz4"),
        ("LZO",
         "Copyright (C) 1996-2017 Markus Franz Xaver Johannes Oberhumer. Dilisensikan di bawah GNU GPL versi 2 atau lebih baru.\n" +
         "https://www.oberhumer.com/opensource/lzo/"),
        (".NET MAUI dan .NET",
         "Copyright (c) .NET Foundation dan kontributor. Dilisensikan di bawah MIT License.\n" +
         "https://github.com/dotnet/maui"),
        ("Tanpa jaminan",
         "Program ini didistribusikan dengan harapan berguna, tetapi TANPA JAMINAN APA PUN; bahkan tanpa jaminan " +
         "tersirat DAPAT DIPERDAGANGKAN atau KESESUAIAN UNTUK TUJUAN TERTENTU. Lihat GNU GPL untuk detailnya."),
    };

    public LicensePage()
    {
        Shell.SetNavBarIsVisible(this, false);
        this.SetAppThemeColor(BackgroundColorProperty, Color.FromArgb("#F2F6FD"), Color.FromArgb("#0A1220"));

        var stack = new VerticalStackLayout { Padding = new Thickness(22, 28, 22, 24), Spacing = 12, MaximumWidthRequest = 560 };

        var back = new Button { Text = "‹ Kembali", BackgroundColor = Colors.Transparent, TextColor = Color.FromArgb("#2F7CF6"), FontSize = 15, Padding = 0 };
        back.Clicked += (_, _) => _ = Navigation.PopAsync();
        var header = new Label { Text = "Lisensi", FontSize = 22, FontAttributes = FontAttributes.Bold, VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.End };
        header.SetAppThemeColor(Label.TextColorProperty, Color.FromArgb("#0E1B33"), Colors.White);
        var top = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) } };
        top.Add(back, 0); top.Add(header, 1);
        stack.Add(top);

        foreach (var (title, body) in Sections)
        {
            var t = new Label { Text = title, FontSize = 15, FontAttributes = FontAttributes.Bold };
            t.SetAppThemeColor(Label.TextColorProperty, Color.FromArgb("#0E1B33"), Colors.White);
            var b = new Label { Text = body, FontSize = 13 };
            b.SetAppThemeColor(Label.TextColorProperty, Color.FromArgb("#3A4A66"), Color.FromArgb("#C3D0E6"));
            var card = new Border
            {
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 16 },
                StrokeThickness = 1,
                Padding = new Thickness(16, 14),
                Content = new VerticalStackLayout { Spacing = 6, Children = { t, b } },
            };
            card.SetAppTheme<Brush>(Border.StrokeProperty, new SolidColorBrush(Color.FromArgb("#D5DFEF")), new SolidColorBrush(Color.FromArgb("#22335A")));
            card.SetAppThemeColor(BackgroundColorProperty, Colors.White, Color.FromArgb("#111C30"));
            stack.Add(card);
        }

        Content = new ScrollView { Content = stack };
    }
}
