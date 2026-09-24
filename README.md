# TruyenDex Clone — Angular / ASP.NET Core / PostgreSQL

Bản triển khai giao diện và luồng đọc truyện dựa trên [TruyenDex NetTrom](https://truyendex.xyz/nettrom). **Dùng API và ảnh thật**, không dùng dữ liệu truyện giả. Trang chủ giữ thứ tự mới cập nhật → cũ nhất của nguồn, phân trang 28 truyện.

## Chạy bằng Docker

Yêu cầu Docker Desktop đang chạy và PowerShell 7 (`pwsh`). Tại thư mục dự án:

```powershell
pwsh -NoProfile -File scripts/setup.ps1
docker compose up -d --build
```

- Website: http://localhost:4200
- API health: http://localhost:5080/api/health
- PostgreSQL: `localhost:54329`, database/user: `truyendex`.
- Mật khẩu DB, khóa JWT và tài khoản quản trị được sinh ngẫu nhiên trong `.env`, không đưa vào Git. Không cần tài khoản quản trị để đọc truyện; đăng ký người đọc trực tiếp ở `/dang-ky`.
- Dừng: `docker compose down`. Dữ liệu nằm trong volume `postgres_data`; không thêm `-v` nếu muốn giữ dữ liệu.

## Chạy phát triển

Yêu cầu Node.js 24, .NET SDK 10, Docker Desktop và PowerShell 7.

```powershell
pwsh -NoProfile -File scripts/setup.ps1
docker compose up -d db
dotnet tool restore
dotnet run --project backend/TruyenDex.Api --no-launch-profile --urls http://localhost:5080
```

Trong terminal thứ hai:

```powershell
cd frontend
npm ci
npm start -- --host 127.0.0.1 --port 4200 --proxy-config proxy.conf.json
```

Frontend chuyển `/api` đến .NET qua proxy cùng origin. EF Core tự áp dụng migration khi API khởi động. Không chạy cả bản Docker web/API và bản native trên cùng cổng.

## Các chức năng

- Trang chủ tối, hàng đề cử, lưới truyện/chương cập nhật và cột xếp hạng; responsive desktop/mobile.
- Danh sách mới → cũ dùng đúng endpoint trang chủ của TruyenDex; phân trang lấy dữ liệu theo nhu cầu, không quét hàng loạt toàn bộ kho truyện.
- Tìm theo tên, thể loại, trạng thái, quốc gia, đối tượng, ngôn ngữ, năm; sắp xếp mới cập nhật, mới xuất bản, theo dõi, đánh giá, tên.
- Chi tiết truyện, tác giả, mô tả, thẻ thể loại, thống kê nguồn và danh sách chương phân trang/đảo thứ tự.
- Đọc ảnh chương thật, chọn chương, chương trước/sau, phím mũi tên; ảnh lazy-load, báo lỗi và tải lại từng ảnh; chế độ ảnh gốc/tiết kiệm, tùy chỉnh chiều rộng.
- Đăng ký/đăng nhập; mật khẩu băm bằng ASP.NET Core PasswordHasher, JWT hết hạn sau 7 ngày, giới hạn tần suất đăng nhập.
- Theo dõi, lịch sử và đánh giá lưu PostgreSQL theo người dùng. Khách chưa đăng nhập có lịch sử cục bộ trên thiết bị.
- Bình luận riêng của bản clone; tác giả bình luận và tài khoản quản trị có quyền xóa. Không gửi bình luận/đánh giá lên TruyenDex.
- Cài đặt sáng/tối, ngôn ngữ mặc định khi tìm kiếm và đọc chương, chất lượng ảnh; lưu trên thiết bị.

## Kiến trúc / nguồn dữ liệu

```text
Angular 21 ── /api ── ASP.NET Core 10 ── PostgreSQL 17
                           │
                           ├─ api.truyendex.cc/api/series/homepage
                           └─ api-proxy.truyendex.cc/mangadex/*
                              fallback: api-proxy.truyendex.xyz/mangadex/*
                              fallback: api.mangadex.org/*
```

- Ảnh bìa: `services.f-ck.me/v1/image/` theo định dạng website mẫu.
- Ảnh chương: nhận `baseUrl`, `hash`, `data`, `dataSaver` từ `/at-home/server/{id}` rồi trình duyệt tải ảnh trực tiếp.
- API nguồn được cache 2 phút; lần gọi chưa cache được xếp hàng và cách nhau tối thiểu 350ms. Không nhận URL proxy tùy ý từ người dùng.
- PostgreSQL lưu thông tin truyện/chương đã truy cập để làm khóa ngoại, người dùng, theo dõi, lịch sử, đánh giá và bình luận; không tải toàn bộ ảnh về DB.
- `backend/TruyenDex.Api/Catalog.cs`: bộ chuyển đổi nguồn dữ liệu.
- `backend/TruyenDex.Api/Program.cs`: API, xác thực, kiểm tra quyền và lưu trữ.
- `backend/TruyenDex.Api/Migrations`: schema có phiên bản.
- `frontend/src/app`: các trang Angular tải lười theo route.

## Kiểm tra

```powershell
dotnet build backend/TruyenDex.Api
cd frontend
npm run build
cd ..
node scripts/smoke.mjs
```

Smoke test dùng API thật và tạo hai tài khoản kiểm thử riêng, kiểm tra 14 tình huống: kết nối DB, quyền truy cập, validation, thứ tự mới/cũ, phân trang, tìm kiếm, chương/ảnh, xác thực, theo dõi idempotent, lịch sử, đánh giá, bình luận, cách ly người dùng và quyền xóa. Bình luận/theo dõi/lịch sử thử được gỡ cuối bài kiểm tra; tài khoản và đánh giá thử được giữ trong DB phát triển.

## Giới hạn hiện tại

- Đây là bản tái tạo độc lập, chưa phải bản sao pixel-perfect hoặc đầy đủ mọi tính năng tài khoản nội bộ của website gốc. Đề cử dùng truyện được đánh giá cao; xếp hạng lấy thống kê hiện tại của MangaDex, không tính riêng theo tháng.
- Mô tả, ảnh, lịch chương và độ sẵn có phụ thuộc API công khai của nguồn, chưa có SLA. Chương đã bị gỡ/không có bản dịch sẽ hiển thị trạng thái trống. Tìm kiếm của MangaDex giới hạn 10.000 kết quả; trang chủ sử dụng phân trang riêng của TruyenDex.
- Trang chủ là feed tiếng Việt của TruyenDex. Cài đặt ngôn ngữ áp dụng cho tìm kiếm, xếp hạng và danh sách chương mới mở.
- Không đồng bộ tài khoản/bình luận cũ của website nguồn, không có chức năng sửa/xóa truyện ở nguồn, khôi phục mật khẩu qua email hoặc lịch phát hành nhà xuất bản.
- Cấu hình Docker hiện tại chỉ mở cổng trên loopback. Khi đưa lên máy chủ cần HTTPS, quản lý secret, sao lưu PostgreSQL và điều chỉnh cấu hình triển khai phù hợp.

Tham chiếu được kiểm tra ngày 24/09/2026. Quyền sở hữu nội dung thuộc các tác giả/nhà phát hành và nguồn tương ứng.
