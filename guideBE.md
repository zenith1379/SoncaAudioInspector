# guideBE - Bootstrap Auth, Remember Login, Visual AI

## 1. Build

```powershell
# Mở PowerShell tại thư mục gốc của project (ví dụ: D:\PROJECT\Git Trending\SoncaAudioInspector)
dotnet build
```

Build hiện chạy được. Warning `NU1701` của `SkiaSharp.Views.WPF` vẫn còn do package target cũ hơn `net9.0-windows`; warning này chưa chặn app chạy.

## 2. Bootstrap app apiKey

Registry dùng:

```txt
HKCU\Software\SoncaAudioInspector\Auth
```

Value chính:

```txt
AppSession
RememberedLogin
```

Cả hai value đều là Base64 của dữ liệu đã mã hóa bằng DPAPI `CurrentUser`, không lưu plaintext.

Test reset bootstrap:

```powershell
if (Test-Path "HKCU:\Software\SoncaAudioInspector") {
  Remove-Item -Path "HKCU:\Software\SoncaAudioInspector" -Recurse -Force -ErrorAction SilentlyContinue
}
```

Tạo `verify.txt` tại thư mục gốc của project (ứng dụng sẽ tự động quét và tìm thấy ở đây):

```powershell
@'
{
  "email": "<admin-app-email>",
  "password": "<admin-app-password>"
}
'@ | Set-Content -LiteralPath "verify.txt" -Encoding UTF8
```

Nếu chạy bằng nút xanh Visual Studio với platform `Any CPU` hoặc `x64`, app cũng sẽ tìm `verify.txt` ở các vị trí sau:

```txt
<AppContext.BaseDirectory>\verify.txt
<ProjectRoot>\verify.txt
<ProjectRoot>\bin\Debug\net9.0-windows\verify.txt
<ProjectRoot>\bin\x64\Debug\net9.0-windows\verify.txt
```

Chạy app:

```powershell
dotnet run
```

Kết quả đúng:

- App đọc `verify.txt`.
- Gọi `POST /api/app/verify`.
- Nhận `apiKey`.
- Mã hóa bằng DPAPI.
- Lưu vào `HKCU`.
- Xóa `verify.txt`.
- Không lưu email/password admin app sau verify.
- Tắt app hoặc reboot Windows không làm mất `AppSession`.

Kiểm tra Registry:

```powershell
if (Test-Path "HKCU:\Software\SoncaAudioInspector\Auth") {
  Get-ItemProperty -Path "HKCU:\Software\SoncaAudioInspector\Auth" |
    Select-Object AppSession, RememberedLogin
} else {
  Write-Host "Registry key HKCU:\Software\SoncaAudioInspector\Auth không tồn tại." -ForegroundColor Yellow
}
```

Kiểm tra `verify.txt` đã bị xóa:

```powershell
Test-Path "verify.txt"
```

Phải trả về:

```txt
False
```

## 3. Reboot và re-bootstrap khi Admin đổi password

Test giữ apiKey sau reboot:

1. Bootstrap thành công một lần.
2. Kiểm tra `AppSession` tồn tại trong Registry.
3. Tắt app.
4. Restart Windows hoặc sign out/sign in lại đúng Windows user.
5. Chạy lại app khi không có `verify.txt`.

Kết quả đúng:

- App đọc lại `AppSession` từ `HKCU`.
- DPAPI decrypt được vì vẫn là cùng Windows user.
- App không mất apiKey chỉ vì tắt/mở máy.
- Nếu backend vẫn chấp nhận app key, LoginWindow mở bình thường.

Các trường hợp app cần `verify.txt` mới:

- Registry bị xóa.
- Chạy bằng Windows user khác.
- Server trả `APP_KEY_INVALID`.
- Server trả `APP_KEY_EXPIRED`.
- Admin đổi password và phát hành bootstrap credential mới.

Test:

```powershell
if (Test-Path "HKCU:\Software\SoncaAudioInspector\Auth") {
  Remove-ItemProperty -Path "HKCU:\Software\SoncaAudioInspector\Auth" -Name AppSession -ErrorAction SilentlyContinue
}
```

Sau đó phát hành lại `verify.txt` mới vào output folder và chạy app.

Kết quả đúng:

- Nếu `AppSession` hợp lệ thì app dùng luôn, không cần `verify.txt`.
- Nếu `AppSession` không có / invalid / server từ chối thì app đọc `verify.txt` mới.
- Verify thành công thì ghi lại `AppSession`.
- `verify.txt` bị secure delete.

## 4. Staff login và remembered password

Login staff dùng:

```txt
POST /api/app/login
Header: X-App-Api-Key: <app api key>
Body: account/password của staff
```

Test remembered password:

1. Login bằng tài khoản staff.
2. Tick `Ghi nhớ đăng nhập trên máy này`.
3. Đăng nhập thành công.
4. Đóng app, mở lại.
5. LoginWindow phải tự điền account/password và tick checkbox.

Kiểm tra Registry:

```powershell
if (Test-Path "HKCU:\Software\SoncaAudioInspector\Auth") {
  Get-ItemProperty -Path "HKCU:\Software\SoncaAudioInspector\Auth" |
    Select-Object RememberedLogin
}
```

Kết quả đúng:

- Có `RememberedLogin`.
- Nội dung là chuỗi mã hóa, không thấy password plaintext.

Test clear remembered password:

1. Mở app.
2. Bỏ tick `Ghi nhớ đăng nhập trên máy này`.
3. Login thành công.
4. App xóa `RememberedLogin`.

Kiểm tra:

```powershell
if (Test-Path "HKCU:\Software\SoncaAudioInspector\Auth") {
  (Get-ItemProperty -Path "HKCU:\Software\SoncaAudioInspector\Auth" -ErrorAction SilentlyContinue).RememberedLogin
}
```

Phải rỗng hoặc báo property không còn tồn tại.

Lưu ý bảo mật: remembered password chỉ nên bật trên máy QA/dev tin cậy. Dữ liệu đã được DPAPI bảo vệ theo Windows user hiện tại, nhưng đây vẫn là opt-in cache.

## 5. Lỗi cần test cho login

Sai password staff:

- Server trả lỗi.
- LoginWindow hiện message lỗi.
- Không mở MainWindow.
- Không ghi remembered password mới.

Server chưa connect được:

- App hiện lỗi kết nối.
- Không crash.
- Nếu appApiKey đã cache hợp lệ nhưng login server down thì vẫn giữ app ở LoginWindow.

App apiKey invalid:

- App gọi lại bootstrap nếu có `verify.txt`.
- Nếu không có `verify.txt`, app báo thiếu bootstrap credential.

## 6. Sync model/sản phẩm từ server

App hiện load model theo thứ tự:

1. Local `checking_config.json`.
2. Gọi server lấy danh sách sản phẩm/model.
3. Merge model từ server vào `ComboModels`.

API desktop đang gọi:

```txt
GET /api/products?page=1&pageSize=100
GET /api/products/serial/{serialNumber}
```

Server response nên có một trong các field:

```txt
model
productCode
name
```

Test:

1. Login thành công.
2. Mở MainWindow.
3. Mở dropdown model.
4. Model local vẫn còn.
5. Model từ server xuất hiện thêm nếu chưa trùng.

Nếu server down, app vẫn dùng local config và không crash.

## 7. Ngoại Quan AI

Tab `Ngoại Quan AI` đã dùng module từ:

```txt
D:\PROJECT\Iphone_detect\Iphone_detect_1_1\AiVisualization
```

File tích hợp chính:

```txt
D:\PROJECT\Git Trending\SoncaAudioInspector\AiVisualization\YoloDetector.cs
D:\PROJECT\Git Trending\SoncaAudioInspector\VisualAI.xaml
D:\PROJECT\Git Trending\SoncaAudioInspector\VisualAI.xaml.cs
```

Model ONNX được tìm theo thứ tự:

1. Env var `SONCA_AI_MODEL`.
2. `D:\PROJECT\Iphone_detect_YOLO26_plan\yolo26n.onnx`.
3. `bin\Debug\net9.0-windows\models\visual-ai.onnx`.

Set model bằng env var để test:

```powershell
$env:SONCA_AI_MODEL = "D:\path\to\model.onnx"
dotnet run
```

Hoặc copy model vào output:

```powershell
# Tự động tìm thư mục build net9.0-windows và copy model vào đó
$out = Get-ChildItem -Path "bin" -Recurse -Directory -Filter "net9.0-windows" | Select-Object -First 1 -ExpandProperty FullName
if ($out) {
  New-Item -ItemType Directory -Force -Path "$out\models"
  Copy-Item "D:\path\to\model.onnx" "$out\models\visual-ai.onnx" -Force
} else {
  Write-Host "Không tìm thấy thư mục build net9.0-windows. Hãy chạy 'dotnet build' trước." -ForegroundColor Red
}
```

Test ảnh tĩnh:

1. Mở tab `Ngoại Quan AI`.
2. Bấm `Import`.
3. Chọn ảnh sản phẩm.
4. Bấm `Detect`.
5. Khung bên phải vẽ bounding box.
6. Badge hiện `PASS` nếu label thuộc nhóm pass, ngược lại `FAIL`.

Test camera:

1. Chọn `DroidCam` hoặc `USB Camera 0`.
2. Nếu DroidCam, nhập IP/port/path.
3. Bấm `Connect`.
4. Có hình live ở panel trái.
5. Bấm `Capture`.
6. Bấm `Detect`.
7. Bấm `Disconnect` khi xong.

Nếu chưa có model hợp lệ, UI báo lỗi load model và không crash.

## 8. Upload ảnh Ngoại Quan AI vào log QA

Khi đang có sản phẩm được chọn, tab `Ngoại Quan AI` sẽ upload ảnh lên server để tạo log QA:

```txt
POST /api/app/qa-visual
Authorization: Bearer <staff access token>
Content-Type: multipart/form-data
```

Form data:

```txt
productId=<id sản phẩm>
status=PENDING|PASS|FAIL
note=<ghi chú AI>
file=<ảnh jpg>
```

Backend xử lý:

- Check staff session bằng Bearer token.
- Rate limit upload.
- Lưu ảnh bằng `storeQaLogImage`.
- Blob key của ảnh QA Visual dùng format:

```txt
qa_logs/<productId>/<timestamp>-visual-ai-<productId>-<yyyyMMddHHmmss>.jpg
```

- Nếu có `BLOB_READ_WRITE_TOKEN` thì ảnh được lưu trên Vercel Blob.
- Nếu chưa có `BLOB_READ_WRITE_TOKEN` trong môi trường dev thì fallback lưu local trong `public/uploads`.
- Tạo log QA trong database với `imageUrl`.
- Response trả thêm `storageProvider`:
  - `vercel-blob`: ảnh đã lưu trên Vercel Blob.
  - `local`: backend đang fallback local, sẽ không thấy ảnh trong Vercel Storage.

Desktop sau khi upload sẽ hiện một trong hai trạng thái:

```txt
Đã upload QA log <logId> (Vercel Blob)
Đã upload QA log <logId> (local uploads)
```

Nếu thấy `(local uploads)` thì tích hợp upload đã chạy, nhưng backend hiện tại không có `BLOB_READ_WRITE_TOKEN` nên ảnh không nằm trong Vercel Storage.

File server:

```txt
D:\PROJECT\Speaker Inventory System\src\app\api\app\qa-visual\route.ts
D:\PROJECT\Speaker Inventory System\src\lib\storage.ts
```

Production Vercel đã deploy route này:

```txt
https://speaker-inventory-system.vercel.app/api/app/qa-visual
```

Vercel Blob kiểm tra theo directory:

```txt
qa_logs/<productId>
```

Ví dụ trên UI Vercel Storage, đổi query `directory=` thành:

```txt
directory=qa_logs%2F<productId>
```

Test cần làm:

1. Start backend với env database và auth bình thường.
2. Nếu muốn test Vercel Blob thật ở local, thêm env trước khi `npm run dev`:

```powershell
cd "D:\PROJECT\Speaker Inventory System"
$env:BLOB_READ_WRITE_TOKEN = "<vercel-blob-token>"
npm run dev
```

3. Nếu desktop cần trỏ về backend local thay vì production Vercel:

```powershell
$env:SONCA_API_BASE_URL = "http://localhost:3000"
dotnet run --project "D:\PROJECT\Git Trending\SoncaAudioInspector\SoncaAudioInspector.csproj" -p:Platform=x64
```

4. Nếu test production Vercel, cần deploy server có route `/api/app/qa-visual` và cấu hình env trên Vercel Project:

```txt
BLOB_READ_WRITE_TOKEN=<token từ Vercel Blob store>
```

Sau khi set env trên Vercel phải redeploy app.

5. Chạy desktop app.
6. Login staff thành công.
7. Ở màn hình chính, nhập serial và bấm kiểm tra để app set sản phẩm hiện tại.
8. Mở tab `Ngoại Quan AI`.
9. Bấm `Capture`.

Kết quả đúng sau `Capture`:

- App gửi ảnh hiện tại lên `POST /api/app/qa-visual`.
- Server tạo log QA status `PENDING`.
- Log có `imageUrl`.
- Nếu status app hiện `(Vercel Blob)`, ảnh sẽ thấy trong Vercel Storage.
- Nếu status app hiện `(local uploads)`, ảnh chỉ nằm ở backend local `public/uploads`, không thấy trong Vercel Storage.
- Nếu dùng Vercel Blob, `imageUrl` là URL blob và response có `storageProvider=vercel-blob`.
- Nếu dev local không có token blob, `imageUrl` trỏ về `/uploads/...` và response có `storageProvider=local`.
- UI phải hiện dạng:

```txt
Đã upload QA PENDING log <logId> (Vercel Blob)
```

Lưu ý: `Capture` chỉ là chụp ảnh thô nên status là `PENDING`. Muốn có kết quả `PASS` hoặc `FAIL`, cần bấm `Detect`.

Flow API upload ảnh QA Visual:

1. Desktop login staff:

```txt
POST /api/app/login
Header: X-App-Api-Key: <app api key>
Body: account/password
```

2. Server trả `accessToken`.
3. Desktop giữ `accessToken` trong memory.
4. Desktop chọn sản phẩm bằng serial/model flow hiện có.
5. Khi bấm `Capture`, desktop encode frame thành JPEG và upload status `PENDING`.
6. Desktop gọi:

```txt
POST https://speaker-inventory-system.vercel.app/api/app/qa-visual
Authorization: Bearer <staff accessToken>
X-App-Api-Key: <app api key>
Content-Type: multipart/form-data

productId=<productId>
status=PENDING
note=Visual AI capture
file=<jpeg>
```

7. Server xác thực staff token.
8. Server upload file lên Vercel Blob:

```txt
qa_logs/<productId>/<timestamp>-visual-ai-<productId>-<yyyyMMddHHmmss>.jpg
```

9. Server tạo record trong `qa_logs` với `image_url=<blob url>`.
10. Server trả:

```json
{
  "data": {
    "logId": "...",
    "productId": "...",
    "checkType": "QA",
    "status": "PENDING",
    "imageUrl": "https://...",
    "key": "qa_logs/<productId>/...",
    "storageProvider": "vercel-blob"
  }
}
```

11. Desktop hiện `Đã upload QA PENDING log <logId> (Vercel Blob)`.

App có ghi log local cho từng lần upload, không chứa token/password:

```powershell
Get-Content "$env:LOCALAPPDATA\SoncaAudioInspector\visual-ai-upload.log" -Tail 20
```

Mỗi dòng có dạng:

```txt
server=https://speaker-inventory-system.vercel.app | productId=... | status=PENDING/PASS/FAIL | http=201 | storage=vercel-blob | key=qa_logs/<productId>/... | imageUrl=https://...
```

Nếu không thấy dòng mới sau khi bấm `Capture`/`Detect`, nghĩa là app đang chưa chạy bản build mới hoặc chưa vào đúng flow upload.

Test detect:

1. Import ảnh hoặc capture từ camera.
2. Bấm `Detect`.

Kết quả đúng sau `Detect`:

- App upload ảnh đã annotate bounding box.
- Nếu AI đánh giá lỗi thì status `FAIL`.
- Nếu AI không thấy lỗi thì status `PASS`.
- Note có dạng `Visual AI detect: PASS/FAIL; detections=...; elapsedMs=...`.
- UI phải hiện dạng:

```txt
Đã upload QA PASS log <logId> (Vercel Blob)
Đã upload QA FAIL log <logId> (Vercel Blob)
```

Flow API của `Detect` giống `Capture`, nhưng form data đổi:

```txt
status=PASS hoặc FAIL
note=Visual AI detect: PASS/FAIL; detections=...; elapsedMs=...
file=<ảnh đã vẽ bounding box>
```

Nếu chưa chọn sản phẩm:

- App không upload.
- UI báo `Chưa chọn sản phẩm, ảnh chưa upload`.
- Cần quay lại màn hình chính, kiểm tra serial trước, rồi capture/detect lại.

Test lỗi server:

- Tắt backend rồi bấm `Capture`.
- App không crash.
- UI hiển thị lỗi upload/log.

Test thiếu Blob token:

1. Xóa hoặc không set `BLOB_READ_WRITE_TOKEN`.
2. Start backend local.
3. Capture ảnh.

Kết quả:

- App vẫn tạo QA log nếu auth/database OK.
- App hiện `(local uploads)`.
- Ảnh nằm ở:

```txt
D:\PROJECT\Speaker Inventory System\public\uploads\qa_logs\<productId>\...
```

- Vercel Storage không có ảnh mới.

Test auth hết hạn:

- Revoke refresh/access token hoặc logout staff.
- Bấm `Capture`.
- Server trả unauthorized.
- App hiện lỗi cần login lại, không lưu ảnh local kèm credential.

Kiểm tra log trong DB:

```sql
select id, product_id, staff_id, check_type, status, image_url, created_at
from qa_logs
order by created_at desc
limit 10;
```

Nếu schema đang dùng bảng `qc_logs` thay vì `qa_logs`, kiểm tra bảng tương ứng theo migration hiện tại.

## 9. Logout và revoke refresh token

Logout hiện gọi async:

```txt
POST /api/auth/logout
Body: { refreshToken }
```

Sau đó app xóa session local trong memory.

Test tránh khóa 2 device:

1. Login staff trên máy A.
2. Logout bằng nút trong app.
3. Server phải revoke refresh token/device session cũ.
4. Login lại cùng staff trên máy A hoặc máy B.
5. Không còn lỗi bị giữ device cũ.

Nếu server offline lúc logout:

- App vẫn clear local session.
- Server có thể vẫn giữ refresh token cũ, cần backend timeout/revoke policy xử lý thêm.

## 10. Log kiểm tra nhanh

Build:

```powershell
dotnet build
```

Run:

```powershell
dotnet run
```

Scan credential leakage trước khi commit:

```powershell
# Sử dụng công cụ ripgrep (nếu đã cài đặt)
rg -n "password|adminapp|refreshToken|accessToken|apiKey|AppSession|RememberedLogin" .

# Hoặc sử dụng lệnh PowerShell thuần túy không cần cài đặt thêm phần mềm bên ngoài
Get-ChildItem -Recurse -File -Exclude *.dll,*.exe,*.png,*.jpg,*.rar | Select-String -Pattern "password|adminapp|refreshToken|accessToken|apiKey|AppSession|RememberedLogin"
```

Kết quả chấp nhận:

- Chỉ thấy tên field/key hoặc placeholder.
- Không có password thật.
- Không có accessToken/refreshToken/apiKey thật.

## 11. Troubleshooting nhanh

Nếu bấm nút xanh Visual Studio báo `can not find file specified`:

1. Kiểm tra Startup Project phải là `SoncaAudioInspector`, không phải folder/project phụ.
2. Chọn platform có trong solution: `Any CPU` hoặc `x64`.
3. Đóng instance app cũ nếu đang chạy:

```powershell
if (Get-Process -Name SoncaAudioInspector -ErrorAction SilentlyContinue) {
  Stop-Process -Name SoncaAudioInspector -Force
}
```

4. Build lại:

```powershell
dotnet build
dotnet build SoncaAudioInspector.csproj -p:Platform=x64
```

5. Nếu lỗi nhắc file cụ thể, kiểm tra file đó nằm trong output:

```powershell
Get-ChildItem "bin\Debug\net9.0-windows" -ErrorAction SilentlyContinue
Get-ChildItem "bin\x64\Debug\net9.0-windows" -ErrorAction SilentlyContinue
```

Nếu `dotnet run` báo `Invalid API key`:

1. Có thể `AppSession` trong Registry vẫn decrypt được nhưng server đã revoke/expire key.
2. App sẽ xóa cache và bootstrap lại nếu server trả `APP_KEY_INVALID`, `APP_KEY_EXPIRED`, `INVALID_API_KEY`, hoặc message dạng `Invalid API key`.
3. Cần đặt `verify.txt` mới vào output folder trước khi chạy lại:

```powershell
```powershell
@'
{
  "email": "<admin-app-email>",
  "password": "<admin-app-password>"
}
'@ | Set-Content -LiteralPath "verify.txt" -Encoding UTF8
```

4. Nếu muốn ép bootstrap lại ngay:

```powershell
if (Test-Path "HKCU:\Software\SoncaAudioInspector\Auth") {
  Remove-ItemProperty -Path "HKCU:\Software\SoncaAudioInspector\Auth" -Name AppSession -ErrorAction SilentlyContinue
}
dotnet run
```
