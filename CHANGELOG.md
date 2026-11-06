# Changelog

## [Unreleased]

### Added
- Tab "Trang dự án" cài được `nmco3.dll`, `ehsvc.dll`, `hsupdate.exe`, `CAPlay.dll`, `ddraw.dll` cùng launcher (hoặc riêng). Mỗi bản phát hành có `attachments.json`; trang chỉ đề nghị các tệp khác với bản đang cài (so SHA-256).
- `ehsvc.dll` và `hsupdate.exe` tự dựng bằng .NET Native AOT (`ClientStubs/`), thay cho hai tệp HackShield; `CheckmateCaChildPe` chỉnh PE của `hsupdate.exe` sau publish.
- Tài nguyên của máy chủ (`name.idx` + `name.idd`) nằm riêng trong `from-oca\`, kèm `load-order.txt` cho CAPlay; đổi máy chủ thì thư mục được dọn.

### Changed
- Máy chủ không còn cài được tệp chạy (dll/exe) vào máy người chơi; manifest cũ chỉ còn nhận tệp dữ liệu (`.idx`, `.idd`, `.ssd`) ở thư mục game. Danh sách `.remove` của máy chủ không còn hiệu lực.
- Chi tiết: `docs/components-and-resources.md`.

## [1.0.2] - 2026-11-06
- Không đổi chức năng: bản vá để thử workflow chỉ chạy khi có tag.

## [1.0.1] - 2026-11-06
- Không đổi chức năng: bản vá để thử quy trình phát hành mới (mô tả release lấy từ CHANGELOG).

## [1.0.0] - 2026-11-06
- Bản đầu tiên của launcher OpenCrazyArcade (`AutoUpdate.exe`) và `nmco3.dll`.
- Tự cập nhật: nhận bản mới từ GitHub Releases, kiểm SHA-256 trước khi thay.
- Cấu hình máy chủ nhanh bằng liên kết `opencrazyarcade://set-server/?src=...`.
- Chạy một bản duy nhất (mutex) và điều khiển bản đang chạy qua named pipe.
