using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Ardalis.GuardClauses;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.eShopWeb.ApplicationCore.Entities;
using Microsoft.eShopWeb.ApplicationCore.Entities.BasketAggregate;
using Microsoft.eShopWeb.ApplicationCore.Entities.OrderAggregate;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;
using Microsoft.eShopWeb.ApplicationCore.Specifications;

namespace Microsoft.eShopWeb.ApplicationCore.Services;

public class OrderService : IOrderService
{
    private readonly IRepository<Order> _orderRepository;
    private readonly IUriComposer _uriComposer;
    private readonly IRepository<Basket> _basketRepository;
    private readonly IRepository<CatalogItem> _itemRepository;

    public OrderService(IRepository<Basket> basketRepository,
        IRepository<CatalogItem> itemRepository,
        IRepository<Order> orderRepository,
        IUriComposer uriComposer)
    {
        _orderRepository = orderRepository;
        _uriComposer = uriComposer;
        _basketRepository = basketRepository;
        _itemRepository = itemRepository;
    }

    public async Task CreateOrderAsync(int basketId, Address shippingAddress)
    {
        var basketSpec = new BasketWithItemsSpecification(basketId);
        var basket = await _basketRepository.FirstOrDefaultAsync(basketSpec);

        Guard.Against.Null(basket, nameof(basket));
        Guard.Against.EmptyBasketOnCheckout(basket.Items);

        var catalogItemsSpecification = new CatalogItemsSpecification(basket.Items.Select(item => item.CatalogItemId).ToArray());
        var catalogItems = await _itemRepository.ListAsync(catalogItemsSpecification);

        var items = basket.Items.Select(basketItem =>
        {
            var catalogItem = catalogItems.First(c => c.Id == basketItem.CatalogItemId);
            var itemOrdered = new CatalogItemOrdered(catalogItem.Id, catalogItem.Name, _uriComposer.ComposePicUri(catalogItem.PictureUri));
            var orderItem = new OrderItem(itemOrdered, basketItem.UnitPrice, basketItem.Quantity);
            return orderItem;
        }).ToList();

        var order = new Order(basket.BuyerId, shippingAddress, items);

        await _orderRepository.AddAsync(order);

        var content = JsonContent.Create(items.Select(item => new { catalogItemId = item.ItemOrdered.CatalogItemId, units = item.Units }));
        // Service Bus integration
        await using var client = new ServiceBusClient("order-items.servicebus.windows.net", new DefaultAzureCredential());

        var sender = client.CreateSender("orders");
        var message = new ServiceBusMessage(await content.ReadAsStringAsync());

        // Send a message
        await sender.SendMessageAsync(message);

        
        var deliveryContent = JsonContent.Create(
            new OrderDelivery
            {
                id = Guid.NewGuid().ToString(),
                FinalPrice = order.Total(),
                OrderItems = order.OrderItems.Select(item => new OrderDeliveryItem
                {
                    CatalogueItemId = item.ItemOrdered.CatalogItemId,
                    UnitPrice = item.UnitPrice,
                    Units = item.Units
                }).ToList(),
                ShippingAddress = order.ShipToAddress
            });
        await new HttpClient().PostAsync("https://order-items.azurewebsites.net/api/OrderDeliveryProcessor?code=secret", deliveryContent);
    }

    private class OrderDelivery
    {
        public string id { get; set; }
        public Address ShippingAddress { get; set; }

        public List<OrderDeliveryItem> OrderItems { get; set; }
        public decimal FinalPrice { get; set; }
    }

    private class OrderDeliveryItem
    {
        public int Units { get; set; }
        public decimal UnitPrice { get; set; }
        public int CatalogueItemId { get; set; }
    }
}
