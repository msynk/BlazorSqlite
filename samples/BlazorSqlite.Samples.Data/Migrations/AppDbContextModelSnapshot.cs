using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace BlazorSqlite.Samples.Data.Migrations;

[DbContext(typeof(AppDbContext))]
public sealed class AppDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.11");

        modelBuilder.Entity("BlazorSqlite.Samples.Domain.Category", b =>
        {
            b.Property<int>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("INTEGER");
            b.Property<string>("Color")
                .IsRequired()
                .HasMaxLength(16)
                .HasColumnType("TEXT");
            b.Property<string>("Name")
                .IsRequired()
                .HasMaxLength(100)
                .HasColumnType("TEXT");
            b.HasKey("Id");
            b.ToTable("Categories");
        });

        modelBuilder.Entity("BlazorSqlite.Samples.Domain.Customer", b =>
        {
            b.Property<int>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("INTEGER");
            b.Property<decimal>("CreditLimit")
                .HasPrecision(18, 2)
                .HasColumnType("TEXT");
            b.Property<DateTime>("CreatedUtc")
                .HasColumnType("TEXT");
            b.Property<DateOnly>("DateOfBirth")
                .HasColumnType("TEXT");
            b.Property<string>("DisplayName")
                .IsRequired()
                .HasMaxLength(120)
                .HasColumnType("TEXT");
            b.Property<string>("Email")
                .IsRequired()
                .HasMaxLength(200)
                .HasColumnType("TEXT");
            b.Property<bool>("IsVip")
                .HasColumnType("INTEGER");
            b.Property<string>("Notes")
                .IsRequired()
                .HasMaxLength(500)
                .HasColumnType("TEXT");
            b.Property<Guid>("PublicId")
                .HasColumnType("TEXT");
            b.HasKey("Id");
            b.HasIndex("Email")
                .IsUnique();
            b.HasIndex("PublicId")
                .IsUnique();
            b.ToTable("Customers");
        });

        modelBuilder.Entity("BlazorSqlite.Samples.Domain.OrderLine", b =>
        {
            b.Property<int>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("INTEGER");
            b.Property<int>("OrderId")
                .HasColumnType("INTEGER");
            b.Property<int>("ProductId")
                .HasColumnType("INTEGER");
            b.Property<int>("Quantity")
                .HasColumnType("INTEGER");
            b.Property<decimal>("UnitPrice")
                .HasPrecision(18, 2)
                .HasColumnType("TEXT");
            b.HasKey("Id");
            b.HasIndex("OrderId");
            b.HasIndex("ProductId");
            b.ToTable("OrderLines");
        });

        modelBuilder.Entity("BlazorSqlite.Samples.Domain.Product", b =>
        {
            b.Property<int>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("INTEGER");
            b.Property<int>("CategoryId")
                .HasColumnType("INTEGER");
            b.Property<DateTime>("CreatedUtc")
                .HasColumnType("TEXT");
            b.Property<DateOnly?>("DiscontinuedOn")
                .HasColumnType("TEXT");
            b.Property<bool>("IsActive")
                .HasColumnType("INTEGER");
            b.Property<TimeSpan>("LeadTime")
                .HasColumnType("TEXT");
            b.Property<string>("Name")
                .IsRequired()
                .HasMaxLength(200)
                .HasColumnType("TEXT");
            b.Property<decimal>("Price")
                .HasPrecision(18, 2)
                .HasColumnType("TEXT");
            b.Property<Guid>("PublicId")
                .HasColumnType("TEXT");
            b.Property<string>("Sku")
                .IsRequired()
                .HasMaxLength(32)
                .HasColumnType("TEXT");
            b.Property<string>("Tags")
                .IsRequired()
                .HasMaxLength(200)
                .HasColumnType("TEXT");
            b.Property<double>("WeightKg")
                .HasColumnType("REAL");
            b.HasKey("Id");
            b.HasIndex("CategoryId");
            b.HasIndex("Name");
            b.HasIndex("PublicId")
                .IsUnique();
            b.HasIndex("Sku")
                .IsUnique();
            b.ToTable("Products");
        });

        modelBuilder.Entity("BlazorSqlite.Samples.Domain.SalesOrder", b =>
        {
            b.Property<int>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("INTEGER");
            b.Property<int>("CustomerId")
                .HasColumnType("INTEGER");
            b.Property<string>("Notes")
                .IsRequired()
                .HasMaxLength(500)
                .HasColumnType("TEXT");
            b.Property<string>("Number")
                .IsRequired()
                .HasMaxLength(24)
                .HasColumnType("TEXT");
            b.Property<DateTimeOffset>("OrderedAt")
                .HasColumnType("TEXT");
            b.Property<DateOnly?>("ShipBy")
                .HasColumnType("TEXT");
            b.Property<string>("Status")
                .IsRequired()
                .HasMaxLength(16)
                .HasColumnType("TEXT");
            b.HasKey("Id");
            b.HasIndex("CustomerId");
            b.HasIndex("Number")
                .IsUnique();
            b.ToTable("Orders", (string?)null);
        });

        modelBuilder.Entity("BlazorSqlite.Samples.Domain.OrderLine", b =>
        {
            b.HasOne("BlazorSqlite.Samples.Domain.SalesOrder", "Order")
                .WithMany("Lines")
                .HasForeignKey("OrderId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
            b.HasOne("BlazorSqlite.Samples.Domain.Product", "Product")
                .WithMany("Lines")
                .HasForeignKey("ProductId")
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired();
            b.Navigation("Order");
            b.Navigation("Product");
        });

        modelBuilder.Entity("BlazorSqlite.Samples.Domain.Product", b =>
        {
            b.HasOne("BlazorSqlite.Samples.Domain.Category", "Category")
                .WithMany("Products")
                .HasForeignKey("CategoryId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
            b.Navigation("Category");
        });

        modelBuilder.Entity("BlazorSqlite.Samples.Domain.SalesOrder", b =>
        {
            b.HasOne("BlazorSqlite.Samples.Domain.Customer", "Customer")
                .WithMany("Orders")
                .HasForeignKey("CustomerId")
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired();
            b.Navigation("Customer");
        });

        modelBuilder.Entity("BlazorSqlite.Samples.Domain.Category", b => b.Navigation("Products"));
        modelBuilder.Entity("BlazorSqlite.Samples.Domain.Customer", b => b.Navigation("Orders"));
        modelBuilder.Entity("BlazorSqlite.Samples.Domain.Product", b => b.Navigation("Lines"));
        modelBuilder.Entity("BlazorSqlite.Samples.Domain.SalesOrder", b => b.Navigation("Lines"));
    }
}
