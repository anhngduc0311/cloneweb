using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TruyenDex.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPasswordVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "PasswordVersion",
                table: "Users",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PasswordVersion",
                table: "Users");
        }
    }
}
