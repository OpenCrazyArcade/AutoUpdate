# Changelog

## [Unreleased]

- Bản thử: không kèm `ddraw.dll` và `CAPlay.dll` (chỉ các tệp dựng bởi workflow).

## [1.3.7] - 2026-11-07
### Fixed
- Nút "Bắt đầu" hiện "Đang Cập Nhật..." (và bị tắt) trong lúc launcher tự cài các tệp client còn thiếu lúc mở, thay vì vẫn hiện như đã sẵn sàng.


## [1.3.6] - 2026-11-07
### Fixed
- Sau khi tự đồng bộ tệp client lúc mở, launcher báo lại cho trang dự án để trang cập nhật (trước đó trang vẫn hiện "lệch N tệp" vì đã hỏi trước khi đồng bộ xong).


## [1.3.5] - 2026-11-07
- Bản thử: bản đầy đủ cuối của loạt 1.3.x.

## [1.3.4] - 2026-11-07
- Bản thử: `CAPlay.dll` trở lại.

## [1.3.3] - 2026-11-07
- Bản thử: không kèm `CAPlay.dll`.

## [1.3.2] - 2026-11-07
- Bản thử: `ddraw.dll` trở lại.

## [1.3.1] - 2026-11-07
- Bản thử: không kèm `ddraw.dll`.

## [1.3.0] - 2026-11-07
- Bản thử 1.3.x để kiểm tra giao diện cập nhật: bản đầy đủ, không đổi chức năng.

## [1.1.4] - 2026-11-07
### Added
- Mở launcher thì tự đưa các tệp client (`nmco3.dll`, `ehsvc.dll`, `hsupdate.exe`, `CAPlay.dll`, `ddraw.dll`) về đúng bản của launcher (theo `attachments.json` của bản đó, so SHA-256): chỉ cần chép `AutoUpdate.exe` vào thư mục game. Launcher vẫn không tự dò bản launcher mới; đổi/hạ phiên bản launcher vẫn qua trang dự án.


## [1.1.3] - 2026-11-07
- Bản thử: `ddraw.dll` trở lại bản phát hành.

## [1.1.2] - 2026-11-07
- Bản thử: phát hành không kèm `ddraw.dll` để kiểm tra bản phát hành thiếu tệp (trang dự án chỉ đề nghị các tệp có trong bản).

## [1.1.1] - 2026-11-07
- Bản thử để kiểm tra nâng cấp từ 1.1.0: không đổi chức năng.

## [1.1.0] - 2026-11-07
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
