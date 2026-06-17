# 📬 Hướng Dẫn Test API Bằng Postman

## Thông Tin Chung

| Thông số | Giá trị |
|----------|---------|
| **Base URL** | `https://speaker-inventory-system.vercel.app` |
| **Auth scheme** | `Bearer` token (dùng `apiKey` nhận được từ bước verify) |

---

## Bước 1: Đăng Nhập & Lấy API Key

### Request

```
POST https://speaker-inventory-system.vercel.app/api/app/verify
```

### Cấu hình trong Postman

1. Mở Postman → tạo **New Request**
2. Method: **POST**
3. URL: `https://speaker-inventory-system.vercel.app/api/app/verify`
4. Tab **Body** → chọn **raw** → chọn **JSON**
5. Nhập body:

```json
{
  "email": "qa.tran",
  "password": "Staff@123A"
}
```

> [!TIP]
> Bạn có thể dùng `email` hoặc `username` đều được. Ví dụ:
> - `"email": "qa.tran"` (dùng username)
> - `"email": "qa.tran@speaker.local"` (dùng email đầy đủ)

6. Nhấn **Send**

### Response Thành Công (200 OK)

```json
{
  "data": {
    "apiKey": "sis_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
    "tokenType": "Bearer",
    "apiKeyExpiresAt": "2026-06-17T10:00:00.000Z",
    "apiKeyExpiresIn": 7200,
    "user": {
      "staffId": "STF-QA-001",
      "username": "qa.tran",
      "email": "qa.tran@speaker.local",
      "name": "Tran Hoang Nam",
      "role": "STAFF"
    }
  }
}
```

7. **Copy** giá trị `data.apiKey` (ví dụ: `sis_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx`)

---

## Bước 2: Gọi API Được Bảo Vệ

### Ví dụ: Lấy danh sách sản phẩm

### Request

```
GET https://speaker-inventory-system.vercel.app/api/products?page=1&pageSize=5
```

### Cấu hình trong Postman

1. Tạo **New Request**
2. Method: **GET**
3. URL: `https://speaker-inventory-system.vercel.app/api/products?page=1&pageSize=5`
4. Tab **Authorization**:
   - Type: **Bearer Token**
   - Token: paste `apiKey` vừa copy ở Bước 1
5. Nhấn **Send**

### Kết quả mong đợi

Server trả về JSON chứa danh sách sản phẩm.

---

## Tài Khoản Test

### Staff Account

| Field | Value |
|-------|-------|
| Email | `qa.tran@speaker.local` |
| Username | `qa.tran` |
| Password | `Staff@123A` |
| Role | `STAFF` |

### Admin App Account (quản lý API keys)

| Field | Value |
|-------|-------|
| Email | `adminapp@speaker.local` |
| Username | `adminapp@speaker.local` |
| Password | `Adminapp@123A` |
| Role | `ADMINAPP` |

> [!NOTE]
> Tài khoản ADMINAPP chỉ dùng trên website để quản lý API keys (cập nhật thời hạn, thu hồi key).
> Tài khoản này không dùng được trong app desktop vì role `ADMINAPP` không thuộc `ADMIN` hoặc `STAFF`.

---

## Lưu Ý Quan Trọng

> [!IMPORTANT]
> - **API Key có thời hạn**: mặc định `7200 giây` (2 giờ). Khi hết hạn, cần gọi lại `/api/app/verify` để lấy key mới.
> - **Không có app session token**: Hệ thống mới chỉ dùng 1 bước duy nhất (verify + login gộp chung).
> - **Header Authorization**: Luôn dùng format `Bearer sis_xxx...` cho mọi request cần xác thực.

---

## Cách Dùng Biến Trong Postman (Nâng Cao)

Để tiện test nhiều request, bạn có thể tạo **Collection Variables**:

| Variable | Value |
|----------|-------|
| `baseUrl` | `https://speaker-inventory-system.vercel.app` |
| `apiKey` | *(paste sau khi verify)* |

Sau đó dùng trong URL:
```
{{baseUrl}}/api/app/verify
{{baseUrl}}/api/products?page=1&pageSize=5
```

Và trong tab Authorization:
```
Type: Bearer Token
Token: {{apiKey}}
```

---

## Xử Lý Lỗi Thường Gặp

| Lỗi | Nguyên nhân | Cách sửa |
|-----|-------------|----------|
| `Error: getaddrinfo ENOTFOUND api` | URL thiếu domain, chỉ nhập `/api/...` | Dùng full URL: `https://speaker-inventory-system.vercel.app/api/...` |
| `401 Unauthorized` | Sai email/password hoặc API key hết hạn | Kiểm tra lại thông tin đăng nhập hoặc lấy key mới |
| `403 Forbidden` | Tài khoản không có quyền | Kiểm tra role của tài khoản |
| `Could not send request` | Không có kết nối internet | Kiểm tra mạng, thử ping `speaker-inventory-system.vercel.app` |
