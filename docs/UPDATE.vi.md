# Cập nhật Sonca Audio Inspector

v1.0 (1.0.0) có updater tích hợp. Feed tĩnh mặc định:
`https://inventory.acnos.store/updates/audio-inspector/`. `SONCA_UPDATE_FEED` chỉ dành cho test/staging.
U1 chỉ triển khai và kiểm offline; SSH/nginx/deploy thuộc U2 có quyền mạng.

## Phát hành

1. Tăng `Version`, `AssemblyVersion`, `FileVersion`, `InformationalVersion` trong csproj; giữ `IncludeSourceRevisionInInformationalVersion=false`. Header và build identity dùng `AppVersion`.
2. Tạo portable self-contained win-x64, giữ `libMathNetNumericsMKL.dll`. Thư mục ứng dụng tại trạm phải có quyền ghi; không đặt trong Program Files nếu tài khoản vận hành không có quyền ghi.
3. Chạy `scripts/publish-update.ps1 -PortableDir <portable> -Version 1.0.1 -Notes 'Nội dung thay đổi' [-Previous <feed-cũ>]`. Bỏ `-PortableDir` để gọi `publish-portable.ps1 -Architecture x64`. Output/log nằm trong artifact mới. Khóa ký mặc định là `%USERPROFILE%\.sonca-update\update-signing-key.pem`; có thể chỉ định `-KeyPath`. Không tự sinh khóa.
4. Review manifest, log build/verify, ZIP trong `packages/<version>/`, rồi ở U2 chạy `scripts/deploy-update.ps1 -FeedDir <feed> -WhatIf`. Sau khi review lệnh, chạy không có `-WhatIf`. Máy phát hành cần ssh/scp và rsync. Thêm `deploy/nginx-updates-audio-inspector.conf` vào cả server block 80/443; chạy `nginx -t` trước reload.

Manifest UTF-8 schema 1, app `SoncaAudioInspector`, architecture `win-x64`; signature base64 ECDSA P-256/SHA-256 IEEE P1363 ký đúng byte manifest. `files/<sha256>` là blob bất biến; không sửa blob/version cũ. `versions/<version>.json[.sig]` lưu lịch sử. ZIP chỉ chứa các file được phép cập nhật, máy cài mới khởi tạo dữ liệu trạm riêng. Không nhúng dữ liệu đo/calibration của trạm phát hành vào ZIP.

Deploy đưa files lên trước (bỏ qua hash đã có), rồi versions/packages; upload latest bằng tên tạm, rename từng file nguyên tử. Cặp manifest/chữ ký gồm hai file: client có thể gặp chữ ký lệch trong khoảnh khắc chuyển cặp và sẽ bỏ qua an toàn tới lần mở sau. Không bao giờ áp dụng manifest chưa xác minh. Cuối deploy tải latest và so SHA-256 với bản local.

## Khóa ký

Chỉ người phát hành được đọc private key; giới hạn ACL cho tài khoản đó, không đưa vào Git, portable, feed hoặc log. Sao lưu ít nhất hai bản mã hóa ở nơi tách biệt, kiểm khả năng phục hồi bằng một feed test có kiểm soát. Khóa công khai nhúng trong app là điểm tin cậy, không nhận khóa thay thế từ manifest.
Nếu mất private key, bản app đã cài không thể tin khóa mới qua feed này. Phân phối thủ công bản đầy đủ có public key mới qua kênh tin cậy, rồi mới tiếp tục tự cập nhật. Nếu lộ khóa: ngừng phát feed, thay khóa và phân phối thủ công; không dựa vào chữ ký của khóa đã lộ để đổi điểm tin cậy.

## Rollback và hành vi trạm

Phát hành lại nội dung tốt trước đây dưới **số phiên bản cao hơn**; client bỏ qua phiên bản bằng hoặc thấp hơn. Không hạ số version trực tiếp. Đo/sửa model/đo nâng cao phải rảnh trước khi hỏi và ngay trước thay file. Chọn “Để sau” không hỏi lại trong phiên. Tải chỉ file đổi hoặc thiếu theo size/SHA-256; tải sai thì hủy, chưa thay file nào. Khi thay lỗi, khôi phục toàn bộ file đã đổi. Lỗi hệ thống ngăn rollback thì giữ `.old-*` để phục hồi thủ công; không xóa bản sao cuối cùng. Mở lần sau dọn staging cũ và backup tốt nhất có thể; nếu thiếu file gốc thì phục hồi backup thay vì xóa.

Không xóa file lạ ngoài manifest. Luôn loại trừ ở cả builder và client:

- `checking_config*.json`
- `save standards/**`, `calibrations/**`, `standards/**`, `fail data/**`
- `update-staging/**`, `*.old-*`
- `*.log`, `*.jsonl`
- `Chay_SoncaAudioInspector.bat` (giữ launcher đang có)

Client từ chối đường dẫn tuyệt đối, ổ đĩa, UNC, `..`, tên Windows không hợp lệ, đường dẫn trùng/xung đột, symlink/junction hoặc thoát thư mục app. Cleanup cũng bỏ qua các thư mục dữ liệu trạm. Không dùng updater để phân phối cấu hình/calibration.

## Ghi chú tính năng và trang phiên bản

Viết một file UTF-8, mỗi dòng một tính năng; builder bỏ dòng trống. Chạy ví dụ:

```powershell
scripts/publish-update.ps1 -PortableDir <portable> -Version 1.0.1 -Notes 'Sửa và bổ sung' -FeaturesFile <features.txt> -Released '2026-11-05T03:00:00Z' -Previous <feed-cu>
```

Hai tham số tương ứng của `UpdateFeedTool build` là `--features-file` và `--released`. Bỏ `--released` để dùng giờ UTC hiện tại. `features` là trường tùy chọn nằm trong manifest được ký; client cũ không có trường này vẫn tương thích. Hộp thoại cập nhật hiển thị tối đa tám dòng tính năng và thêm “…” khi còn nữa.

Builder **ghi `version-info.json` vào portable trước khi băm** với `version`, `releasedUtc`, `features`; file này thuộc manifest và ZIP, được cập nhật như file chương trình. Không đưa file này vào loại trừ. Chỉ chạy builder trên thư mục portable chuẩn bị phát hành, không dùng thư mục ứng dụng đang vận hành ở trạm làm đầu vào.

Builder tạo `index.html` một file tĩnh, CSS inline, không JavaScript/tài nguyên ngoài; mọi notes/features được escape HTML. Trang liệt kê các manifest trong `versions/` theo version giảm dần, làm nổi bật bản mới nhất; mỗi bản có ngày UTC, tính năng, link ZIP, dung lượng và SHA-256 của ZIP. Khi dùng `--previous`, các manifest/chữ ký/blob/package trước được kiểm tra và giữ trong feed mới để link lịch sử không bị thiếu. Giữ feed cũ hoặc luôn truyền `-Previous` khi phát hành tiếp.

Ở U2, thêm snippet nginx vào **cả server block 80 và 443**. `/update` và `/update/` redirect 302 tới `/updates/audio-inspector/`, dùng `index index.html`; HTML và latest không cache. `deploy-update.ps1` đưa HTML lên bằng tên tạm và `mv` sau latest, rồi tải `/update` theo redirect để kiểm version. U1/U1b chưa chạy các lệnh mạng này.

## Tab Phiên bản trong ứng dụng

Nhấn **Ctrl+Shift+I**, chọn tab cuối **Phiên bản · v1.0** (tên tự theo `AppVersion.Display`). Tab hiển thị version đang chạy, ngày ghi assembly theo giờ máy, tính năng từ `version-info.json` cạnh exe, kênh cập nhật và trạng thái kiểm gần nhất. Thiếu file hiện ghi chú bản phát triển; JSON sai hoặc version không khớp hiện lỗi ngắn và không đóng ứng dụng.

Nút **Kiểm tra cập nhật** gọi cùng luồng với kiểm khi khởi động, dùng chung trạng thái và lượt đang chạy; chỉ hỏi/thay file khi không đo hoặc sửa model. Nếu đã chọn **Để sau**, phiên hiện tại không hỏi lại, kể cả khi kiểm thủ công; trạng thái vẫn cập nhật. Nút **Lịch sử phiên bản** mở `https://inventory.acnos.store/update` bằng trình duyệt mặc định.

Harness offline đặt `SONCA_UPDATE_DISABLE_AUTO_CHECK=1` để tắt tự kiểm lúc Loaded. FUC mở MeasurementDetailsWindow riêng, đọc release-info giả và dùng callback cục bộ; không khởi tạo MainWindow, không HTTP/audio/BLE và không mở trình duyệt. Cờ này chỉ dành cho kiểm, không bật ở trạm vận hành.
