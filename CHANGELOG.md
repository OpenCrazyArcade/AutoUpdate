# Changelog

## [Unreleased]
- Không đổi chức năng: bản vá để thử workflow chỉ chạy khi có tag.

## [1.0.1] - 2026-11-06
- Không đổi chức năng: bản vá để thử quy trình phát hành mới (mô tả release lấy từ CHANGELOG).

## [1.0.0] - 2026-11-06
- Bản đầu tiên của launcher OpenCrazyArcade (`AutoUpdate.exe`) và `nmco3.dll`.
- Tự cập nhật: nhận bản mới từ GitHub Releases, kiểm SHA-256 trước khi thay.
- Cấu hình máy chủ nhanh bằng liên kết `opencrazyarcade://set-server/?src=...`.
- Chạy một bản duy nhất (mutex) và điều khiển bản đang chạy qua named pipe.
