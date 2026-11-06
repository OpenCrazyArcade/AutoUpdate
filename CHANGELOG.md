# Changelog

## [Unreleased]

### Added
- `ehsvc.dll` và `hsupdate.exe` tự dựng bằng .NET Native AOT (`ClientStubs/`), thay cho hai tệp HackShield; `CheckmateCaChildPe` chỉnh PE của `hsupdate.exe` sau publish. Workflow build, kiểm (ordinal 10, phép kiểm của `CA.exe`) và đính kèm cả hai vào bản phát hành.
- Trang dự án cài được cả `nmco3.dll` cùng launcher (hoặc riêng), mỗi tệp kiểm SHA-256 trước khi cài.

## [1.0.2] - 2026-11-06
- Không đổi chức năng: bản vá để thử workflow chỉ chạy khi có tag.

## [1.0.1] - 2026-11-06
- Không đổi chức năng: bản vá để thử quy trình phát hành mới (mô tả release lấy từ CHANGELOG).

## [1.0.0] - 2026-11-06
- Bản đầu tiên của launcher OpenCrazyArcade (`AutoUpdate.exe`) và `nmco3.dll`.
- Tự cập nhật: nhận bản mới từ GitHub Releases, kiểm SHA-256 trước khi thay.
- Cấu hình máy chủ nhanh bằng liên kết `opencrazyarcade://set-server/?src=...`.
- Chạy một bản duy nhất (mutex) và điều khiển bản đang chạy qua named pipe.
