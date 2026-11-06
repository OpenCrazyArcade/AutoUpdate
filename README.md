# AutoUpdate (launcher OpenCrazyArcade)

Launcher cho game Boom Online (Crazy Arcade) bản VN v116, dùng với các máy chủ riêng của cộng đồng
[OpenCrazyArcade](https://github.com/OpenCrazyArcade). Người chơi tải `AutoUpdate.exe` ở trang
[Releases](https://github.com/OpenCrazyArcade/AutoUpdate/releases); hướng dẫn cài game ở
[trang dự án](https://opencrazyarcade.github.io/huong-dan/).

Repo gồm các dự án sau (`AutoUpdate.slnx`):

| Dự án | Là gì |
|---|---|
| `AutoUpdate/` | Launcher WPF (.NET 10, x86, một file). Chọn máy chủ, cập nhật game, chạy `CA.exe`, giữ cầu nối NMCO. |
| `NMCO3/` | `nmco3.dll` thay cho thư viện messenger của Nexon (Native AOT, x86). Xem `NMCO3/README.md`. |
| `Ipc/` | Kiểu dữ liệu giao thức named pipe dùng chung giữa launcher và `nmco3.dll`. |
| `ClientStubs/EhSvc` | `ehsvc.dll` thay cho thư viện HackShield (Native AOT, x86): `CA.exe` tìm hàm của nó theo **ordinal 10**. |
| `ClientStubs/HSUpdate` | `hsupdate.exe` thay cho bộ cập nhật HackShield (Native AOT, x86); sau publish được chỉnh PE cho đúng phép kiểm của `CA.exe`. |
| `BuildUtils/CheckmateCaChildPe` | Công cụ build chạy sau publish `HSUpdate` (xem README trong thư mục), kèm `.Tests`. |

## Cách hoạt động

- **Vị trí**: chép `AutoUpdate.exe` vào thư mục game, cạnh `ca.exe` (cài game từ bộ cài gốc rồi chép đè). Launcher chỉ tìm `ca.exe` ở đó; không thấy thì không cập nhật, không chạy game, và nhắc kèm link trang hướng dẫn.
- **Máy chủ**: không có máy chủ mặc định. Chủ máy chủ đưa liên kết `opencrazyarcade://set-server/?src=https://...`;
  bấm vào đó, launcher hỏi lại rồi lưu địa chỉ. Cũng nhập tay được trong cửa sổ cấu hình.
- **Một bản duy nhất**: chạy lần hai chỉ chuyển lệnh sang bản đang chạy (mutex + named pipe).
- **Cập nhật từ trang dự án**: launcher không tự dò bản mới. Tab "Trang dự án" liệt kê các bản trên GitHub Releases và các tệp của
  mỗi bản (`AutoUpdate.exe`, `nmco3.dll`, `ehsvc.dll`, `hsupdate.exe`, `CAPlay.dll`, `ddraw.dll`); trang chỉ đề nghị tệp khác với bản đang cài.
  Bấm cài thì launcher hiện địa chỉ tải để xác nhận, tải và kiểm SHA-256 TẤT CẢ các tệp rồi mới cài (ghi đè bản cũ bằng cách đổi tên rồi thay),
  `AutoUpdate.exe` cuối cùng (đổi tên file rồi khởi động lại). Nơi ghi của từng tên do launcher quyết định, không phải trang; tên lạ bị từ chối.
  Cài khi game đang chạy bị từ chối. Chi tiết: [`docs/components-and-resources.md`](docs/components-and-resources.md).
- **Máy chủ chỉ cấp dữ liệu**: gói tài nguyên `name.idx` + `name.idd` nằm riêng trong `from-oca\` (kèm `load-order.txt` cho CAPlay), đổi máy chủ thì thư mục được dọn.
  Máy chủ không cài được tệp chạy nào vào máy người chơi.
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
