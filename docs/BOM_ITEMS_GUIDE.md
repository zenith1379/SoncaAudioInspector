# Quy ước BOM theo model và file Items

## 1. Hai file bắt buộc

Trong App, bấm **Nhập BOM + Items** rồi chọn đồng thời đúng 2 file:

1. `BOM_MODELS.xlsx`: một dòng cho mỗi slot của từng model.
2. `ITEMS.xlsx`: một dòng cho mỗi linh kiện thực tế.

BOM chỉ định nghĩa quy tắc cho model. Không tạo `BOM_001`, `BOM_002` hoặc đăng ký trước từng sản phẩm.
Sản phẩm thực tế chỉ được tạo khi App quét barcode sản phẩm.

## 2. Cột của BOM_MODELS

`schema_version, product_model, slot_index, component_type, component_name, barcode_prefix, required`

Ba model chuẩn:

- `D'AURIS 200` / model key `D200`
- `D'AURIS 300` / model key `D300`
- `D'AURIS 500` / model key `D500`

Mẫu tiền tố barcode:

`ACNOS:BOM:1:<MODEL_KEY>:<COMPONENT_TYPE>:`

Ví dụ slot bo công suất của D'AURIS 300:

`ACNOS:BOM:1:D300:POWER_AMPLIFIER:`

`barcode_prefix` phải kết thúc bằng dấu `:`. Mỗi model không được trùng `slot_index` hoặc `component_type`.

## 3. Cột của ITEMS

`id, component_barcode, image_link, name_items`

- `id`: ID duy nhất và ổn định của item.
- `component_barcode`: tiền tố từ BOM cộng ID linh kiện thực tế, ví dụ `ACNOS:BOM:1:D300:POWER_AMPLIFIER:PA-D300-0001`.
- `image_link`: link chia sẻ HTTPS của Google Drive thuộc tài khoản `hain1974211@gmail.com`; có thể để trống cho đến khi ảnh được tải lên Drive.
- `name_items`: copy đúng `component_name` của dòng BOM tương ứng.

Không dùng `BOM_001`, `BOM_002` trong barcode item. Phần cuối barcode là ID/serial riêng của chính linh kiện.

## 4. Điều kiện App và server kiểm tra

- Barcode phải đúng mẫu `ACNOS:BOM:1:<MODEL>:<TYPE>:<ID>`.
- `<MODEL>` phải khớp model sản phẩm đang quét.
- `<TYPE>` và tiền tố phải khớp slot trong BOM.
- Barcode phải tồn tại trong file ITEMS đã đồng bộ.
- `name_items` phải khớp tên trong BOM.
- Một item không được gắn cho hai sản phẩm; một slot không nhận hai item.
- Link ảnh, nếu có, phải là URL HTTPS của Google Drive/Googleusercontent.

App/server chỉ lưu link Drive và email chủ sở hữu. Việc tải ảnh lên Drive và cấp quyền chia sẻ vẫn thực hiện trong Google Drive của chủ tài khoản; hệ thống không lưu mật khẩu hoặc OAuth token Drive.

## 5. Ảnh nguồn cho bốn loại item

Mapping cố định tại `Assets/Items/item-image-map.json`:

- `POWER_AMPLIFIER` / Bo công suất: `Assets/Items/shopping (1).webp`
- `POWER_SUPPLY` / Bo nguồn: `Assets/Items/shopping.webp`
- `BASS_SPEAKER` / Loa bass: `Assets/Items/shopping (2).webp`
- `MID_SPEAKER` / Loa mid: `Assets/Items/shopping (3).webp`

Bốn ảnh nguồn được đóng gói cùng App. Sau khi tải chúng lên Google Drive của `hain1974211@gmail.com`, copy URL chia sẻ tương ứng vào cột `image_link` của file ITEMS; không dùng đường dẫn ổ đĩa Windows làm `image_link` server.
