namespace TruyenDex.Api;
public record RegisterRequest(string Email, string Password, string Name);
public record LoginRequest(string Email, string Password);
public record CommentRequest(string Body);
public record RatingRequest(int Score);
public record MangaRequest(string Title, string AlternativeTitle, string Author, string Cover, string Description,
    string[] Genres, string Status, string Country, string Demographic, int Year, bool Featured);
public record ChapterRequest(decimal Number, string Title, string Language, string[] Pages);
public record FollowRequest(bool Followed);
