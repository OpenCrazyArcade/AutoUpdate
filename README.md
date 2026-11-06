# AutoUpdate (launcher OpenCrazyArcade)

Launcher cho game Boom Online (Crazy Arcade) bản VN v116, dùng với các máy chủ riêng của cộng đồng
[OpenCrazyArcade](https://github.com/OpenCrazyArcade). Người chơi tải `AutoUpdate.exe` ở trang
[Releases](https://github.com/OpenCrazyArcade/AutoUpdate/releases); hướng dẫn cài game ở
[trang dự án](https://opencrazyarcade.github.io/huong-dan/).

Repo gồm ba dự án (`AutoUpdate.slnx`):

| Dự án | Là gì |
|---|---|
| `AutoUpdate/` | Launcher WPF (.NET 10, x86, một file). Chọn máy chủ, cập nhật game, chạy `CA.exe`, giữ cầu nối NMCO. |
| `NMCO3/` | `nmco3.dll` thay cho thư viện messenger của Nexon (Native AOT, x86). Xem `NMCO3/README.md`. |
| `Ipc/` | Kiểu dữ liệu giao thức named pipe dùng chung giữa launcher và `nmco3.dll`. |

## Cách hoạt động

- **Vị trí**: chép `AutoUpdate.exe` vào thư mục game, cạnh `ca.exe` (cài game từ bộ cài gốc rồi chép đè). Launcher chỉ tìm `ca.exe` ở đó; không thấy thì không cập nhật, không chạy game, và nhắc kèm link trang hướng dẫn.
- **Máy chủ**: không có máy chủ mặc định. Chủ máy chủ đưa liên kết `opencrazyarcade://set-server/?src=https://...`;
  bấm vào đó, launcher hỏi lại rồi lưu địa chỉ. Cũng nhập tay được trong cửa sổ cấu hình.
- **Một bản duy nhất**: chạy lần hai chỉ chuyển lệnh sang bản đang chạy (mutex + named pipe).
- **Tự cập nhật**: launcher không tự dò bản mới. Tab "Trang dự án" liệt kê các bản trên GitHub Releases; bấm cài
  thì launcher hiện địa chỉ tải để xác nhận, tải về, kiểm SHA-256 rồi đổi tên file để thay chính nó.
- **Dữ liệu** nằm ở `C:\ProgramData\OpenCrazyArcade` (`launcher.json`, `logs\`, `Updates\`, `WebView2\`).
- Cần [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/); launcher tự báo nếu thiếu.

## Build

Cần .NET SDK theo `global.json` và (cho `nmco3.dll`) công cụ C++ của Visual Studio.

    dotnet publish AutoUpdate/AutoUpdate.csproj -c Release -p:Platform=x86
    dotnet publish NMCO3/NMCO3.csproj -c Release -p:Platform=x86

Native AOT dừng với `'vswhere.exe' is not recognized` thì đưa thư mục Visual Studio Installer vào `PATH`
(chi tiết trong `NMCO3/README.md`).

## Phát hành

Phiên bản chính là tag git. Workflow `.github/workflows/release.yml` chỉ chạy khi có tag `vX.Y.Z` (hoặc chạy tay),
không chạy khi push lên `main`.

1. Ghi thay đổi dưới `## [Unreleased]` trong `CHANGELOG.md`.
2. `.\tools\release.ps1 1.2.3`: đổi `[Unreleased]` thành `[1.2.3] - ngày`, commit và tạo tag `v1.2.3`.
3. `git push origin main v1.2.3`.

Workflow build `AutoUpdate.exe` và `nmco3.dll`, kiểm số phiên bản trong file khớp tag, rồi tạo Release với mô tả lấy
từ mục `[1.2.3]` của `CHANGELOG.md` kèm SHA-256. Thiếu mục đó thì job dừng, không phát hành. Build hỏng thì không
có release nào được tạo; xoá tag, sửa, tag lại.

## Giấy phép

AGPL-3.0, xem `LICENSE`. Dự án cộng đồng phi thương mại, không liên quan hay liên đới tới Nexon hay chủ sở hữu game.
