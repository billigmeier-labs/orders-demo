using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orders.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderDeliveryNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeliveryNotes",
                table: "Orders",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeliveryNotes",
                table: "Orders");
        }
    }
}
