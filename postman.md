# Postman API Test Notes

Không lưu credential thật trong file này.

## Environment

```txt
baseUrl=https://speaker-inventory-system.vercel.app
appApiKey=
accessToken=
refreshToken=
staffId=
```

## 1. Verify App

```http
POST {{baseUrl}}/api/app/verify
Content-Type: application/json
```

Body:

```json
{
  "email": "<adminapp email>",
  "password": "<adminapp password>"
}
```

Lưu:

```txt
data.appApiKey -> appApiKey
```

## 2. Staff Login

```http
POST {{baseUrl}}/api/app/login
Content-Type: application/json
X-App-Api-Key: {{appApiKey}}
```

Body:

```json
{
  "email": "<staff username or email>",
  "password": "<staff password>"
}
```

Lưu:

```txt
data.accessToken -> accessToken
data.refreshToken -> refreshToken
data.staffId -> staffId
```

## 3. Products

```http
GET {{baseUrl}}/api/products?page=1&pageSize=10
X-App-Api-Key: {{appApiKey}}
Authorization: Bearer {{accessToken}}
```

Có thể thêm keyword nếu backend hỗ trợ:

```http
GET {{baseUrl}}/api/products?page=1&pageSize=10&keyword=<serial-or-model>
```

## 4. Refresh

```http
POST {{baseUrl}}/api/auth/refresh
Content-Type: application/json
X-App-Api-Key: {{appApiKey}}
```

Body:

```json
{
  "refreshToken": "{{refreshToken}}"
}
```

Nếu backend không trả refresh token mới thì giữ refresh token hiện tại.
