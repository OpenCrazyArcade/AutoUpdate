# Changelog

## [Unreleased]

## [1.0.1] - 2026-11-13
### Fixed
- `CmdLine.txt` không còn giữ `loginip`/`loginport` của máy chủ cũ sau khi đổi cấu hình: launcher dựng lại kết nối SignalR khi địa chỉ máy chủ đã khác.

## [1.0.0] - 2026-11-08
### Added
- Launcher `AutoUpdate.exe` cho Boom Online (Crazy Arcade) bản VN v116: đặt cạnh `ca.exe`, chọn máy chủ, cập nhật game, chạy game và giữ cầu nối NMCO. Chạy một bản duy nhất (mutex); chạy lần hai chỉ chuyển lệnh sang bản đang chạy.
- Chọn máy chủ bằng liên kết `opencrazyarcade://set-server/?src=...` (có hộp xác nhận) hoặc nhập tay. Không có máy chủ mặc định.
- Tab "Trang dự án" liệt kê các bản trên GitHub Releases, cài hoặc hạ cấp bản launcher, và cài các tệp client của bản đó (`nmco3.dll`, `ehsvc.dll`, `hsupdate.exe`, `CAPlay.dll`, `ddraw.dll`). Launcher hiện địa chỉ tải để xác nhận, tải và kiểm SHA-256 tất cả các tệp (theo `attachments.json` của bản phát hành) rồi mới ghi đè; từ chối khi game đang chạy.
- Mở launcher thì tự đưa các tệp client về đúng bản của launcher. Launcher không tự dò bản launcher mới.
- `nmco3.dll`, `ehsvc.dll` và `hsupdate.exe` tự dựng bằng .NET Native AOT, thay cho thư viện messenger của Nexon và hai tệp HackShield; `CheckmateCaChildPe` chỉnh PE của `hsupdate.exe` sau publish để qua phép kiểm của `CA.exe`.
- `CAPlay.dll` nạp vào game, liên kết CRT tĩnh nên không cần cài Visual C++ Redistributable. Launcher kiểm kết quả nạp và nói rõ nguyên nhân nếu thất bại.
- `ddraw.dll` (cnc-ddraw, MIT) để chạy game trong cửa sổ.
- Gói tài nguyên của máy chủ (`name.idx` + `name.idd`) nằm riêng trong `from-oca\`, kèm `load-order.txt` cho CAPlay; đổi máy chủ thì thư mục được dọn. Máy chủ chỉ cấp dữ liệu, không cài được tệp chạy vào máy người chơi.
- Trong lúc launcher ghi tệp vào thư mục game, nút "Bắt đầu" và nút cài trên trang dự án bị khoá, kèm trạng thái đang cập nhật.
- Tab trang dự án luôn tải mới, không dùng cache HTTP của WebView.

Chi tiết cách cập nhật tệp: `docs/components-and-resources.md`.
